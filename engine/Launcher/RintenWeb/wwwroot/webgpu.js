//WebGPU object table for the C# renderer: objects live here, C# holds indices
//Per-frame calls take numbers and byte views; build-once descriptors arrive as JSON

(function () {
	"use strict";

	//--- the handle table ------------------------------------------------------- Index zero is null, so a zero handle is "nothing", as it was for
	//the C ABI. Released slots are reused; a stale handle then names something else, which is why the C# side clears its own copy before releasing.

	var objects = [null];
	var free = [];

	function alloc(object) {
		if (!object) return 0;
		var index = free.length ? free.pop() : objects.length;
		objects[index] = object;
		return index;
	}

	function get(handle) {
		return handle ? objects[handle] : null;
	}

	function release(handle) {
		if (!handle) return;
		var object = objects[handle];
		if (!object) return;
		if (typeof object.destroy === "function") {
			try { object.destroy(); } catch (e) { /* already lost with the device */ }
		}
		objects[handle] = null;
		free.push(handle);
	}

	//Lets go of the slot without destroying what is in it. For things the page owns and we only borrowed for
	//a frame - a canvas texture above all, which the browser hands out anew each frame and presents itself.
	function forget(handle) {
		if (!handle) return;
		objects[handle] = null;
		free.push(handle);
	}

	//Unwraps a .NET MemoryView into a typed array view, valid only during the call
	function bytesOf(view) {
		return view && typeof view._unsafe_create_view === "function" ? view._unsafe_create_view() : view;
	}

	globalThis.RintenHandles = { alloc: alloc, get: get, release: release, forget: forget, bytesOf: bytesOf };
})();

(function () {
	"use strict";

	var alloc = RintenHandles.alloc;
	var get = RintenHandles.get;
	var release = RintenHandles.release;
	var bytesOf = RintenHandles.bytesOf;

	//--- the device ---------------------------------------------------------------

	var adapter = null;
	var device = null;
	var queue = null;
	var ready = 0;
	var lost = 0;
	var infoText = "";
	var deviceNameText = "";
	var errorCount = 0;
	var lastError = "";

	async function init(validation) {
		if (!navigator.gpu) {
			infoText = "WebGPU: this browser has none";
			return 0;
		}
		try {
			adapter = await navigator.gpu.requestAdapter({ powerPreference: "high-performance" });
			if (!adapter) {
				infoText = "WebGPU: no adapter";
				return 0;
			}
			var required = [];
			//The three block families a web build's pictures are turned into - see BrowserTextures.CreateUniversal - whichever the device has.
			for (var feature of ["depth32float-stencil8", "texture-compression-bc", "texture-compression-astc", "texture-compression-etc2", "timestamp-query"]) {
				if (adapter.features.has(feature)) required.push(feature);
			}
			device = await adapter.requestDevice({
				requiredFeatures: required,
				requiredLimits: {
					maxStorageBufferBindingSize: Math.min(adapter.limits.maxStorageBufferBindingSize, 256 * 1024 * 1024),
					maxBufferSize: Math.min(adapter.limits.maxBufferSize, 256 * 1024 * 1024)
				}
			});
			queue = device.queue;
			device.lost.then(function (info) {
				lost = 1;
				infoText = "WebGPU: device lost - " + info.message;
			});
			device.onuncapturederror = function (e) {
				errorCount++;
				lastError = e.error && e.error.message ? e.error.message : String(e);
				if (validation) console.error("WebGPU:", lastError);
			};
			var adapterInfo = adapter.info || {};
			deviceNameText = [adapterInfo.vendor, adapterInfo.architecture, adapterInfo.device, adapterInfo.description]
				.filter(function (s) { return s; }).join(" ") || "WebGPU";
			infoText = "WebGPU " + deviceNameText + " (" + navigator.gpu.getPreferredCanvasFormat() + ")";
			ready = 1;
			return 1;
		} catch (e) {
			infoText = "WebGPU: " + (e && e.message ? e.message : String(e));
			return 0;
		}
	}

	function limit(name) {
		return device ? device.limits[name] : 0;
	}

	function hasFeature(name) {
		return device && device.features.has(name) ? 1 : 0;
	}

	//--- the canvas ---------------------------------------------------------------

	function canvasContext(canvasId, width, height) {
		var canvas = canvasId ? document.getElementById(canvasId) : (globalThis.RintenNative && globalThis.RintenNative.canvas) || document.querySelector("canvas");
		if (!canvas) return 0;
		var context = canvas.getContext("webgpu");
		if (!context) return 0;
		context._rintenCanvas = canvas;
		return alloc(context);
	}

	function configureContext(context, width, height, format, usage) {
		var c = get(context);
		if (!c) return;
		if (c._rintenCanvas) {
			if (c._rintenCanvas.width !== width) c._rintenCanvas.width = width;
			if (c._rintenCanvas.height !== height) c._rintenCanvas.height = height;
		}
		//The same bytes viewed as sRGB as well, which is what the world's pass draws into straight - see DrawsOnCanvas in BrowserSwapchain.
		var srgb = format === "rgba8unorm" || format === "bgra8unorm" ? [format + "-srgb"] : [];
		c.configure({ device: device, format: format, usage: usage, alphaMode: "opaque", viewFormats: srgb });
	}

	function unconfigureContext(context) {
		var c = get(context);
		if (c) c.unconfigure();
	}

	//The current texture of a canvas is valid for one frame and presented by the browser when the frame's task ends; there is no present call.
	function currentTexture(context) {
		var c = get(context);
		if (!c) return 0;
		try {
			return alloc(c.getCurrentTexture());
		} catch (e) {
			return 0;
		}
	}

	//--- buffers ----------------------------------------------------------------

	function createBuffer(size, usage, label) {
		return alloc(device.createBuffer({ size: size, usage: usage, label: label || undefined }));
	}

	function createBufferWithData(bytes, usage, label) {
		var data = bytesOf(bytes);
		var size = (data.byteLength + 3) & ~3;
		var buffer = device.createBuffer({ size: size, usage: usage, mappedAtCreation: true, label: label || undefined });
		new Uint8Array(buffer.getMappedRange()).set(data);
		buffer.unmap();
		return alloc(buffer);
	}

	function writeBuffer(buffer, offset, bytes) {
		var b = get(buffer);
		if (b) queue.writeBuffer(b, offset, bytesOf(bytes));
	}

	//--- textures ---------------------------------------------------------------

	//Layers match WebGL2's signature (unused without multiview); multisample count is 4
	//The engine's bit, not WebGPU's, for a texture read as layers even with one - BrowserDevice.TextureUse.Layered
	var LayeredUse = 1 << 20;

	function createTexture(width, height, mips, format, usage, label, layers, samples) {
		usage &= ~LayeredUse;
		return alloc(device.createTexture({
			size: { width: width, height: height, depthOrArrayLayers: Math.max(1, layers || 1) },
			mipLevelCount: mips,
			sampleCount: samples > 1 ? 4 : 1,
			format: format,
			usage: usage,
			label: label || undefined
		}));
	}

	//dimension "2d" is one layer, baseLayer, of an array - what a pass draws into it through; "2d-array" every layer as one
	function createView(texture, format, aspect, baseMip, mipCount, baseLayer, dimension) {
		var t = get(texture);
		if (!t) return 0;
		var desc = { aspect: aspect || "all", baseMipLevel: baseMip || 0 };
		if (format) desc.format = format;
		if (mipCount > 0) desc.mipLevelCount = mipCount;
		if (dimension) desc.dimension = dimension;
		if (dimension === "2d") { desc.baseArrayLayer = baseLayer || 0; desc.arrayLayerCount = 1; }
		return alloc(t.createView(desc));
	}

	function writeTexture(texture, mip, x, y, width, height, bytesPerRow, bytes) {
		var t = get(texture);
		if (!t) return;
		//A block format's rows are rows of blocks, which is what bytesPerRow already says; one image needs no count of them.
		queue.writeTexture(
			{ texture: t, mipLevel: mip, origin: { x: x, y: y, z: 0 } },
			bytesOf(bytes),
			{ offset: 0, bytesPerRow: bytesPerRow },
			{ width: width, height: height, depthOrArrayLayers: 1 });
	}

	function createSampler(json) {
		return alloc(device.createSampler(JSON.parse(json)));
	}

	//--- layouts and groups --- JSON descriptors whose resources are referenced by handle number

	function createBindGroupLayout(json) {
		return alloc(device.createBindGroupLayout(JSON.parse(json)));
	}

	function createPipelineLayout(json) {
		var desc = JSON.parse(json);
		desc.bindGroupLayouts = desc.layouts.map(get);
		delete desc.layouts;
		return alloc(device.createPipelineLayout(desc));
	}

	function createBindGroup(json) {
		var desc = JSON.parse(json);
		desc.layout = get(desc.layout);
		desc.entries = desc.entries.map(function (e) {
			if (e.buffer !== undefined) {
				var resource = { buffer: get(e.buffer) };
				if (e.offset) resource.offset = e.offset;
				if (e.size) resource.size = e.size;
				return { binding: e.binding, resource: resource };
			}
			if (e.textureView !== undefined) return { binding: e.binding, resource: get(e.textureView) };
			if (e.sampler !== undefined) return { binding: e.binding, resource: get(e.sampler) };
			throw new Error("bind group entry names no resource");
		});
		return alloc(device.createBindGroup(desc));
	}

	//--- shaders and pipelines ----------------------------------------------------

	function createShaderModule(wgsl, label) {
		return alloc(device.createShaderModule({ code: wgsl, label: label || undefined }));
	}

	//Compilation messages, for the log: the browser reports them on the module asynchronously, so this is a promise of a string, empty when it compiled.
	async function shaderMessages(module) {
		var m = get(module);
		if (!m) return "no module";
		var info = await m.getCompilationInfo();
		return info.messages
			.filter(function (msg) { return msg.type === "error"; })
			.map(function (msg) { return msg.lineNum + ":" + msg.linePos + " " + msg.message; })
			.join("\n");
	}

	//The descriptor arrives with "vertexModule" / "fragmentModule" / "layout" as handles. Async, as the
	//browser prefers: a pipeline compiles on another thread and the promise resolves when it can be bound.
	async function createRenderPipeline(json) {
		var desc = JSON.parse(json);
		desc.layout = desc.layout ? get(desc.layout) : "auto";
		desc.vertex.module = get(desc.vertexModule);
		delete desc.vertexModule;
		if (desc.fragment) {
			desc.fragment.module = get(desc.fragmentModule);
			delete desc.fragmentModule;
		}
		try {
			return alloc(await device.createRenderPipelineAsync(desc));
		} catch (e) {
			lastError = e && e.message ? e.message : String(e);
			return 0;
		}
	}

	//--- recording ----------------------------------------------------------------

	function createCommandEncoder() {
		return alloc(device.createCommandEncoder());
	}

	//Pass descriptions by token, so JSON is parsed once; same as in webgl2.js
	var passDescs = [];

	function definePass(token, json) {
		passDescs[token] = JSON.parse(json);
	}

	//Everything forgotten, when the backend's own table fills and starts again.
	function forgetPasses() {
		passDescs = [];
	}

	//Attachments arrive as handles: { colors: [{ view, resolve, load, store, clear:[r,g,b,a]
	//}], depth: { view, depthLoad, depthStore, depthClear, stencilLoad, stencilStore } }
	function beginRenderPass(encoder, token) {
		var e = get(encoder);
		if (!e) return 0;
		var desc = passDescs[token];
		if (!desc) return 0;
		var pass = { colorAttachments: [] };
		var timing = gpuSlot(e);
		if (timing) pass.timestampWrites = timing;
		for (var c of desc.colors) {
			if (!c) { pass.colorAttachments.push(null); continue; }
			var a = { view: get(c.view), loadOp: c.load, storeOp: c.store };
			if (c.resolve) a.resolveTarget = get(c.resolve);
			if (c.clear) a.clearValue = { r: c.clear[0], g: c.clear[1], b: c.clear[2], a: c.clear[3] };
			pass.colorAttachments.push(a);
		}
		if (desc.depth) {
			var d = desc.depth;
			var da = { view: get(d.view) };
			if (d.depthLoad) { da.depthLoadOp = d.depthLoad; da.depthStoreOp = d.depthStore; da.depthClearValue = d.depthClear; }
			if (d.stencilLoad) { da.stencilLoadOp = d.stencilLoad; da.stencilStoreOp = d.stencilStore; }
			if (d.depthReadOnly) da.depthReadOnly = true;
			pass.depthStencilAttachment = da;
		}
		return alloc(e.beginRenderPass(pass));
	}

	function setPipeline(pass, pipeline) {
		get(pass).setPipeline(get(pipeline));
	}

	function setBindGroup(pass, index, group, dynamicOffset) {
		var g = get(group);
		if (dynamicOffset >= 0) get(pass).setBindGroup(index, g, [dynamicOffset]);
		else get(pass).setBindGroup(index, g);
	}

	function setVertexBuffer(pass, slot, buffer, offset, size) {
		get(pass).setVertexBuffer(slot, get(buffer), offset, size > 0 ? size : undefined);
	}

	function setIndexBuffer(pass, buffer, format, offset, size) {
		get(pass).setIndexBuffer(get(buffer), format, offset, size > 0 ? size : undefined);
	}

	function setViewport(pass, x, y, width, height, minDepth, maxDepth) {
		get(pass).setViewport(x, y, width, height, minDepth, maxDepth);
	}

	function setScissorRect(pass, x, y, width, height) {
		get(pass).setScissorRect(x, y, width, height);
	}

	function draw(pass, vertexCount, instanceCount, firstVertex, firstInstance) {
		frameDraws++;
		get(pass).draw(vertexCount, instanceCount, firstVertex, firstInstance);
	}

	function drawIndexed(pass, indexCount, instanceCount, firstIndex, baseVertex, firstInstance) {
		frameDraws++;
		get(pass).drawIndexed(indexCount, instanceCount, firstIndex, baseVertex, firstInstance);
	}

	function endPass(pass) {
		var p = get(pass);
		if (p) p.end();
		release(pass);
	}

	//Runs a frame's command stream from BrowserNative in one call instead of one call per command
	var dynamicOffset = new Uint32Array(1);

	//The depth under the scissor back to the far plane, for a depth-only pass - a page of a shadow map baked again, see
	//BrowserSwapchain.ShadowArea. WebGPU clears only whole attachments, so one triangle at depth one is drawn
	var depthClearer = null;

	function clearDepth(p) {
		if (!depthClearer) {
			var module = device.createShaderModule({ code:
				"@vertex fn main(@builtin(vertex_index) i : u32) -> @builtin(position) vec4<f32> {" +
				" return vec4<f32>(select(-1.0, 3.0, i == 1u), select(-1.0, 3.0, i == 2u), 1.0, 1.0); }" });

			depthClearer = device.createRenderPipeline({
				layout: "auto",
				vertex: { module: module, entryPoint: "main" },
				primitive: { topology: "triangle-list" },
				depthStencil: { format: "depth32float", depthWriteEnabled: true, depthCompare: "always" }
			});
		}

		p.setPipeline(depthClearer);
		p.draw(3);
	}

	function runPass(pass, bytes, count, end) {
		if (count > 0) {
			var p = get(pass);
			var v = bytesOf(bytes);
			var w = new Int32Array(v.buffer, v.byteOffset, count);
			var f = new Float32Array(v.buffer, v.byteOffset, count);
			var i = 0;
			while (i < count) {
				switch (w[i]) {
					case 1: if (p) p.setPipeline(get(w[i + 1])); i += 2; break;
					case 2:
						if (p) {
							if (w[i + 3] >= 0) { dynamicOffset[0] = w[i + 3]; p.setBindGroup(w[i + 1], get(w[i + 2]), dynamicOffset, 0, 1); }
							else p.setBindGroup(w[i + 1], get(w[i + 2]));
						}
						i += 4; break;
					case 3: if (p) p.setVertexBuffer(w[i + 1], get(w[i + 2]), w[i + 3] >>> 0, w[i + 4] > 0 ? w[i + 4] >>> 0 : undefined); i += 5; break;
					case 4: if (p) p.setIndexBuffer(get(w[i + 1]), w[i + 2] ? "uint32" : "uint16", w[i + 3] >>> 0, w[i + 4] > 0 ? w[i + 4] >>> 0 : undefined); i += 5; break;
					case 5: if (p) p.setViewport(f[i + 1], f[i + 2], f[i + 3], f[i + 4], f[i + 5], f[i + 6]); i += 7; break;
					case 6: if (p) p.setScissorRect(w[i + 1], w[i + 2], w[i + 3], w[i + 4]); i += 5; break;
					case 7: if (p) { frameDraws++; p.draw(w[i + 1], w[i + 2], w[i + 3], w[i + 4]); } i += 5; break;
					case 8: if (p) { frameDraws++; p.drawIndexed(w[i + 1], w[i + 2], w[i + 3], w[i + 4], w[i + 5]); } i += 6; break;
					case 10: if (p) clearDepth(p); i += 1; break;
					case 9: {
						var b = get(w[i + 1]), size = w[i + 3];
						if (b) queue.writeBuffer(b, w[i + 2] >>> 0, v, (i + 4) * 4, size);
						i += 4 + ((size + 3) >> 2); break;
					}
					default: i = count; break;
				}
			}
		}
		if (end) endPass(pass);
	}

	//Its own submit, ahead of the frame being recorded: what a buffer is grown into is there before anything this frame draws with it.
	function copyBuffer(from, fromOffset, into, intoOffset, size) {
		var e = device.createCommandEncoder();
		e.copyBufferToBuffer(get(from), fromOffset, get(into), intoOffset, size);
		queue.submit([e.finish()]);
	}

	function copyTextureToTexture(encoder, source, sourceMip, destination, destinationMip, width, height) {
		get(encoder).copyTextureToTexture(
			{ texture: get(source), mipLevel: sourceMip },
			{ texture: get(destination), mipLevel: destinationMip },
			{ width: width, height: height, depthOrArrayLayers: 1 });
	}

	//Finishes the encoder and submits in one, which is the only way the renderer ever used the two; the encoder handle is released here.
	function finishAndSubmit(encoder) {
		var e = get(encoder);
		if (!e) return;
		var readback = gpuResolve(e);
		queue.submit([e.finish()]);
		if (readback) gpuRead(readback, e._rintenPasses);
		release(encoder);
	}

	//--- the GPU's clock --- GPU frame time from per-pass timestamp queries, read back a frame later

	var gpuWanted = false;
	var gpuMs = 0;
	var frameDraws = 0;
	var querySet = null;
	var resolveBuffer = null;
	var readbacks = [];
	var readbacksMade = 0;
	var MaxTimedPasses = 32;

	function timing() {
		return device && device.features.has("timestamp-query");
	}

	function gpuSlot(e) {
		if (!gpuWanted || !timing()) return null;
		if (!querySet) {
			querySet = device.createQuerySet({ type: "timestamp", count: MaxTimedPasses * 2 });
			resolveBuffer = device.createBuffer({ size: MaxTimedPasses * 16, usage: GPUBufferUsage.QUERY_RESOLVE | GPUBufferUsage.COPY_SRC });
		}
		var n = e._rintenPasses || 0;
		if (n >= MaxTimedPasses) return null;
		e._rintenPasses = n + 1;
		return { querySet: querySet, beginningOfPassWriteIndex: n * 2, endOfPassWriteIndex: n * 2 + 1 };
	}

	function gpuResolve(e) {
		var n = e._rintenPasses || 0;
		if (!n) return null;
		//A buffer that is not waiting to be read, or none - a frame whose time is not read is better than one that waits for the card.
		var buffer = readbacks.pop();
		if (!buffer) {
			if (readbacksMade >= 4) return null;
			readbacksMade++;
			buffer = device.createBuffer({ size: MaxTimedPasses * 16, usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST });
		}
		e.resolveQuerySet(querySet, 0, n * 2, resolveBuffer, 0);
		e.copyBufferToBuffer(resolveBuffer, 0, buffer, 0, n * 16);
		return buffer;
	}

	function gpuRead(buffer, passes) {
		buffer.mapAsync(GPUMapMode.READ, 0, passes * 16).then(function () {
			var stamps = new BigUint64Array(buffer.getMappedRange(0, passes * 16));
			for (var i = 0; i < passes; i++) {
				var from = stamps[i * 2], to = stamps[i * 2 + 1];
				if (to > from) gpuMs += Number(to - from) / 1e6;
			}
			buffer.unmap();
			readbacks.push(buffer);
		}, function () { });
	}

	//The GPU milliseconds finished since the last ask, or -1 where there is no timer. want says whether to go on timing.
	function gpuTake(want) {
		gpuWanted = !!want;
		if (!timing()) return -1;
		var taken = gpuMs;
		gpuMs = 0;
		return taken;
	}

	//Draws since the last ask.
	function drawsTake() {
		var taken = frameDraws;
		frameDraws = 0;
		return taken;
	}

	//--- diagnostics --------------------------------------------------------------

	function popError() {
		var text = lastError;
		lastError = "";
		return text;
	}

	globalThis.RintenBackends = globalThis.RintenBackends || {};

	globalThis.RintenBackends.webgpu = {
		gpuTake: gpuTake,
		drawsTake: drawsTake,
		xrLayered: function () { return 0; },
		//A headset needs XRGPUBinding, which no browser worth shipping to has. index.html reads this and picks WebGL2 for a build that asks for VR.
		xrCan: function () { return 0; },
		xrNative: function () { return 0; },

		init: init,
		ready: function () { return ready; },
		lost: function () { return lost; },
		info: function () { return infoText; },
		deviceName: function () { return deviceNameText; },
		language: function () { return "wgsl"; },
		errorCount: function () { return errorCount; },
		popError: popError,
		limit: limit,
		hasFeature: hasFeature,
		release: release,
		forget: RintenHandles.forget,

		canvasContext: canvasContext,
		configureContext: configureContext,
		unconfigureContext: unconfigureContext,
		currentTexture: currentTexture,

		createBuffer: createBuffer,
		createBufferWithData: createBufferWithData,
		writeBuffer: writeBuffer,

		createTexture: createTexture,
		createView: createView,
		writeTexture: writeTexture,
		createSampler: createSampler,

		createBindGroupLayout: createBindGroupLayout,
		createPipelineLayout: createPipelineLayout,
		createBindGroup: createBindGroup,

		createShaderModule: createShaderModule,
		shaderMessages: shaderMessages,
		createRenderPipeline: createRenderPipeline,

		//XR stubs: no WebGPU XR path, but both backends must export the same functions
		xrStereo: function () { return 0; },
		xrNative: function () { return 0; },
		xrTune: function ( scale, foveation ) { },
		xrEyeCopy: function ( source, eye, layer ) { return 0; },

		createCommandEncoder: createCommandEncoder,
		definePass: definePass,
		forgetPasses: forgetPasses,
		beginRenderPass: beginRenderPass,
		setPipeline: setPipeline,
		setBindGroup: setBindGroup,
		setVertexBuffer: setVertexBuffer,
		setIndexBuffer: setIndexBuffer,
		setViewport: setViewport,
		setScissorRect: setScissorRect,
		draw: draw,
		drawIndexed: drawIndexed,
		endPass: endPass,
		runPass: runPass,
		transcodeInto: function (texture, bytes, target, blockBytes) { RintenBasis.transcodeInto(texture, bytes, target, blockBytes, writeTexture); },
		transcodeState: function (texture) { return RintenBasis.transcodeState(texture); },
		copyBuffer: copyBuffer,
		copyTextureToTexture: copyTextureToTexture,
		finishAndSubmit: finishAndSubmit
	};
})();
