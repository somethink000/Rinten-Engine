//webgpu.js's API implemented over WebGL2, so the engine's WebGPU backend runs on either
//Bindings are named B/T{group}_{binding}; viewports and scissors flip Y for GL

(function () {
	"use strict";

	var alloc = RintenHandles.alloc;
	var get = RintenHandles.get;
	var release = RintenHandles.release;
	var bytesOf = RintenHandles.bytesOf;

	var gl = null;
	var canvas = null;
	var ready = 0;
	var lost = 0;
	var validation = false;
	var infoText = "";
	var deviceNameText = "";
	var errorCount = 0;
	var lastError = "";
	var halfFloatTargets = false;
	var blocks = null;
	var bcBlocks = false;
	var astcBlocks = false;
	var etc2Blocks = false;
	var uniformAlign = 256;

	function fail(text) {
		errorCount++;
		lastError = text;
		if (validation) console.error("WebGL2:", text);
	}

	//Reports a never-normal condition (e.g. incomplete framebuffer) even with validation off
	function hard(text) {
		errorCount++;
		lastError = text;
		console.error("WebGL2:", text);
	}

	//--- the device ---------------------------------------------------------------

	function findCanvas(canvasId) {
		var found = canvasId ? document.getElementById(canvasId) : null;
		return found || (globalThis.RintenNative && globalThis.RintenNative.canvas) || document.querySelector("canvas");
	}

	async function init(enableValidation) {
		validation = !!enableValidation;
		canvas = findCanvas(null);
		if (!canvas) {
			infoText = "WebGL2: there is no canvas";
			return 0;
		}

		gl = canvas.getContext("webgl2", {
			//Request XR compatibility at creation; makeXRCompatible later can lose the context
			xrCompatible: !!globalThis.RintenWantsXr || !!navigator.xr,
			alpha: false,
			depth: false,
			stencil: false,
			antialias: false,
			premultipliedAlpha: false,
			preserveDrawingBuffer: false,
			powerPreference: "high-performance"
		});

		if (!gl) {
			infoText = "WebGL2: this browser has none";
			return 0;
		}

		canvas.addEventListener("webglcontextlost", function (e) {
			e.preventDefault();
			lost = 1;
			infoText = "WebGL2: the context was lost";
		});

		//The world's picture is half floats; where those cannot be drawn into, 8-bit stands in - see createTexture
		halfFloatTargets = !!gl.getExtension("EXT_color_buffer_float") || !!gl.getExtension("EXT_color_buffer_half_float");
		uniformAlign = gl.getParameter(gl.UNIFORM_BUFFER_OFFSET_ALIGNMENT);

		//The block formats a compiled texture is stored in - all four families or none, as WebGPU's
		//texture-compression-bc is. A desktop's browser has them; a phone's has none, and the backend unpacks instead.
		var s3tc = gl.getExtension("WEBGL_compressed_texture_s3tc");
		var s3tcSrgb = gl.getExtension("WEBGL_compressed_texture_s3tc_srgb");
		var rgtc = gl.getExtension("EXT_texture_compression_rgtc");
		var bptc = gl.getExtension("EXT_texture_compression_bptc");
		if (s3tc && s3tcSrgb && rgtc && bptc) {
			bcBlocks = true;
			blocks = {
				"bc1-rgba-unorm": s3tc.COMPRESSED_RGBA_S3TC_DXT1_EXT,
				"bc1-rgba-unorm-srgb": s3tcSrgb.COMPRESSED_SRGB_ALPHA_S3TC_DXT1_EXT,
				"bc3-rgba-unorm": s3tc.COMPRESSED_RGBA_S3TC_DXT5_EXT,
				"bc3-rgba-unorm-srgb": s3tcSrgb.COMPRESSED_SRGB_ALPHA_S3TC_DXT5_EXT,
				"bc4-r-unorm": rgtc.COMPRESSED_RED_RGTC1_EXT,
				"bc5-rg-unorm": rgtc.COMPRESSED_RED_GREEN_RGTC2_EXT,
				"bc7-rgba-unorm": bptc.COMPRESSED_RGBA_BPTC_UNORM_EXT,
				"bc7-rgba-unorm-srgb": bptc.COMPRESSED_SRGB_ALPHA_BPTC_UNORM_EXT,
				"bc6h-rgb-ufloat": bptc.COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT_EXT
			};
		}

		//And the two families a web build's pictures are turned into where there is no BC - see
		//BrowserTextures.CreateUniversal: ASTC on a headset or a phone, ETC2 on nearly every phone. Each on its own.
		var astc = gl.getExtension("WEBGL_compressed_texture_astc");
		if (astc) {
			blocks = blocks || {};
			blocks["astc-4x4-unorm"] = astc.COMPRESSED_RGBA_ASTC_4x4_KHR;
			blocks["astc-4x4-unorm-srgb"] = astc.COMPRESSED_SRGB8_ALPHA8_ASTC_4x4_KHR;
			astcBlocks = true;
		}

		var etc = gl.getExtension("WEBGL_compressed_texture_etc");
		if (etc) {
			blocks = blocks || {};
			blocks["etc2-rgb8unorm"] = etc.COMPRESSED_RGB8_ETC2;
			blocks["etc2-rgb8unorm-srgb"] = etc.COMPRESSED_SRGB8_ETC2;
			blocks["etc2-rgba8unorm"] = etc.COMPRESSED_RGBA8_ETC2_EAC;
			blocks["etc2-rgba8unorm-srgb"] = etc.COMPRESSED_SRGB8_ALPHA8_ETC2_EAC;
			etc2Blocks = true;
		}

		var debug = gl.getExtension("WEBGL_debug_renderer_info");
		deviceNameText = (debug ? gl.getParameter(debug.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER)) || "WebGL2";
		infoText = "WebGL2 " + deviceNameText;

		defaultState();
		ready = 1;
		return 1;
	}

	function limit(name) {
		if (!gl) return 0;
		switch (name) {
			case "maxTextureDimension2D": return gl.getParameter(gl.MAX_TEXTURE_SIZE);
			case "maxUniformBufferBindingSize": return gl.getParameter(gl.MAX_UNIFORM_BLOCK_SIZE);
			case "minUniformBufferOffsetAlignment": return uniformAlign;
			case "maxColorAttachments": return gl.getParameter(gl.MAX_DRAW_BUFFERS);
			case "maxVertexAttributes": return gl.getParameter(gl.MAX_VERTEX_ATTRIBS);
			default: return 0;
		}
	}

	//The one optional WebGPU feature the backend asks about: block formats
	function hasFeature(name) {
		switch (name) {
			case "texture-compression-bc": return bcBlocks ? 1 : 0;
			case "texture-compression-astc": return astcBlocks ? 1 : 0;
			case "texture-compression-etc2": return etc2Blocks ? 1 : 0;
			default: return 0;
		}
	}

	//--- the canvas --------------------------------------------------------------- The context was made with the
	//device, since in WebGL they are one thing. The canvas texture is the default framebuffer, borrowed for a frame.

	function canvasContext(canvasId, width, height) {
		var found = findCanvas(canvasId);
		if (!found || found !== canvas) return 0;
		return alloc({ canvas: found });
	}

	function configureContext(context, width, height, format, usage) {
		var c = get(context);
		if (!c) return;
		if (c.canvas.width !== width) c.canvas.width = width;
		if (c.canvas.height !== height) c.canvas.height = height;
	}

	function unconfigureContext(context) {
	}

	function currentTexture(context) {
		var c = get(context);
		if (!c) return 0;
		return alloc({ canvas: true, width: c.canvas.width, height: c.canvas.height });
	}

	//--- buffers --- GL fixes a buffer's kind on first bind, so dual-use buffers wait until first draw

	var Usage = { Index: 16, Vertex: 32 };
	var boundElements = null;

	//Cached ARRAY_BUFFER binding to skip redundant bindBuffer calls out of wasm
	var boundArray = null;

	function Buffer(size, usage) {
		this.size = size;
		this.gl = null;
		this.kind = 0;
		this.early = null;

		var index = (usage & Usage.Index) !== 0;
		var vertex = (usage & Usage.Vertex) !== 0;

		if (index && vertex) this.early = new Uint8Array(size);
		else this.make(index ? gl.ELEMENT_ARRAY_BUFFER : gl.ARRAY_BUFFER, null);
	}

	Buffer.prototype.make = function (kind, bytes) {
		this.kind = kind;
		this.gl = gl.createBuffer();
		this.bind();
		if (bytes) gl.bufferData(kind, bytes, gl.DYNAMIC_DRAW);
		else gl.bufferData(kind, this.size, gl.DYNAMIC_DRAW);
	};

	Buffer.prototype.bind = function () {
		if (this.kind === gl.ELEMENT_ARRAY_BUFFER) {
			if (boundElements !== this.gl) {
				gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.gl);
				boundElements = this.gl;
			}
		} else if (boundArray !== this.gl) {
			gl.bindBuffer(gl.ARRAY_BUFFER, this.gl);
			boundArray = this.gl;
		}
	};

	//What a draw reads it as, made so the first time.
	Buffer.prototype.as = function (kind) {
		if (this.gl) {
			if (this.kind !== kind) {
				fail("a buffer drawn with as both indices and something else");
				return null;
			}
			return this.gl;
		}
		this.make(kind, this.early);
		this.early = null;
		return this.gl;
	};

	Buffer.prototype.write = function (offset, bytes) {
		if (this.early) {
			this.early.set(bytes, offset);
			return;
		}
		this.bind();
		gl.bufferSubData(this.kind, offset, bytes);
	};

	Buffer.prototype.destroy = function () {
		if (this.gl) {
			if (boundElements === this.gl) boundElements = null;
			if (boundArray === this.gl) boundArray = null;
			gl.deleteBuffer(this.gl);
		}
		this.gl = null;
		this.early = null;
	};

	//One buffer into another; a buffer still holding its bytes here is read or written here too
	function copyBuffer(from, fromOffset, into, intoOffset, size) {
		var a = get(from), b = get(into);
		if (!a || !b) return;

		if (a.early) {
			b.write(intoOffset, a.early.subarray(fromOffset, fromOffset + size));
		} else if (b.early) {
			gl.bindBuffer(gl.COPY_READ_BUFFER, a.gl);
			gl.getBufferSubData(gl.COPY_READ_BUFFER, fromOffset, b.early, intoOffset, size);
		} else {
			gl.bindBuffer(gl.COPY_READ_BUFFER, a.gl);
			gl.bindBuffer(gl.COPY_WRITE_BUFFER, b.gl);
			gl.copyBufferSubData(gl.COPY_READ_BUFFER, gl.COPY_WRITE_BUFFER, fromOffset, intoOffset, size);
		}
	}

	function createBuffer(size, usage, label) {
		return alloc(new Buffer(size, usage));
	}

	function createBufferWithData(bytes, usage, label) {
		var data = bytesOf(bytes);
		var buffer = new Buffer((data.byteLength + 3) & ~3, usage);
		buffer.write(0, data);
		return alloc(buffer);
	}

	function writeBuffer(buffer, offset, bytes) {
		var b = get(buffer);
		if (b) b.write(offset, bytesOf(bytes));
	}

	//--- textures ---------------------------------------------------------------

	//The engine's bit for a texture read as layers even with one - must match BrowserDevice.TextureUse.Layered
	var LayeredUse = 1 << 20;

	//WebGPU's format names, as WebGL's sized formats: internal, format, type.
	function formatOf(name, renderable) {
		if (blocks && blocks[name] !== undefined)
			return { internal: blocks[name], compressed: true, depth: false };

		switch (name) {
			case "rgba8unorm-srgb":
				return { internal: gl.SRGB8_ALPHA8, format: gl.RGBA, type: gl.UNSIGNED_BYTE, depth: false, texel: 4 };
			case "rgba16float":
				//Half floats are always sampleable in ES 3.0; the extension is for drawing into them, so a sampled-only
				//picture (the sky table) keeps halves and a drawn one falls back
				if (halfFloatTargets || !renderable) return { internal: gl.RGBA16F, format: gl.RGBA, type: gl.HALF_FLOAT, depth: false, texel: 8 };
				return { internal: gl.RGBA8, format: gl.RGBA, type: gl.UNSIGNED_BYTE, depth: false, texel: 4 };
			case "rgb9e5ufloat":
				return { internal: gl.RGB9_E5, format: gl.RGB, type: gl.UNSIGNED_INT_5_9_9_9_REV, depth: false, texel: 4 };
			case "depth32float":
				return { internal: gl.DEPTH_COMPONENT32F, format: gl.DEPTH_COMPONENT, type: gl.FLOAT, depth: true };
			case "depth24plus":
				return { internal: gl.DEPTH_COMPONENT24, format: gl.DEPTH_COMPONENT, type: gl.UNSIGNED_INT, depth: true };
			default:
				return { internal: gl.RGBA8, format: gl.RGBA, type: gl.UNSIGNED_BYTE, depth: false, texel: 4 };
		}
	}

	//Layers > 1 makes a 2D array (multiview eyes); samples > 1 makes a renderbuffer resolved by blit
	function Texture(width, height, mips, format, usage, layers, samples) {
		this.width = width;
		this.height = height;
		this.layers = Math.max(1, layers || 1);
		//An array whatever its layers where the engine asked for one - a shadow map read as layers - see LayeredUse
		this.target = this.layers > 1 || (usage & LayeredUse) !== 0 ? gl.TEXTURE_2D_ARRAY : gl.TEXTURE_2D;
		this.format = formatOf(format, (usage & 16) !== 0);
		this.framebuffers = [];

		if (samples > 1) {
			this.gl = null;
			this.samples = Math.min(samples, gl.getParameter(gl.MAX_SAMPLES) || 1);
			this.renderbuffer = gl.createRenderbuffer();
			gl.bindRenderbuffer(gl.RENDERBUFFER, this.renderbuffer);
			gl.renderbufferStorageMultisample(gl.RENDERBUFFER, this.samples, this.format.internal, width, height);
			gl.bindRenderbuffer(gl.RENDERBUFFER, null);
			return;
		}

		this.gl = gl.createTexture();

		gl.bindTexture(this.target, this.gl);

		if (this.target === gl.TEXTURE_2D_ARRAY)
			gl.texStorage3D(this.target, Math.max(1, mips), this.format.internal, width, height, this.layers);
		else
			gl.texStorage2D(this.target, Math.max(1, mips), this.format.internal, width, height);

		//WebGL cannot filter depth, and a filtering sampler makes it unreadable; these are its unsampled settings
		gl.texParameteri(this.target, gl.TEXTURE_MIN_FILTER, this.format.depth ? gl.NEAREST : gl.LINEAR);
		gl.texParameteri(this.target, gl.TEXTURE_MAG_FILTER, this.format.depth ? gl.NEAREST : gl.LINEAR);
		units.touched(this.gl);
	}

	Texture.prototype.destroy = function () {
		//A picture drawing into a headset layer holds the browser's texture (xrAlias); give it back first, or
		//deleting it takes an image from the headset's swapchain and that eye freezes
		if (xrOwn && xrOwn.picture === this) xrUnalias();

		for (var i = 0; i < this.framebuffers.length; i++) forgetFramebuffer(this.framebuffers[i]);
		this.framebuffers.length = 0;
		if (this.renderbuffer) {
			gl.deleteRenderbuffer(this.renderbuffer);
			this.renderbuffer = null;
			return;
		}
		units.drop(this.gl);
		gl.deleteTexture(this.gl);
		this.gl = null;
	};

	//Sixteen is WebGPU's RENDER_ATTACHMENT - see BrowserDevice.TextureUse, which is where these numbers come from.
	function createTexture(width, height, mips, format, usage, label, layers, samples) {
		return alloc(new Texture(width, height, mips, format, usage, layers, samples));
	}

	//A view is the texture and its first level; WebGL binds textures, and nothing views part of one otherwise
	//A one-layer view of an array ("2d") is what a pass draws into that layer through; others are the whole texture
	function createView(texture, format, aspect, baseMip, mipCount, baseLayer, dimension) {
		var t = get(texture);
		if (!t) return 0;
		var layer = dimension === "2d" && t.target === gl.TEXTURE_2D_ARRAY ? (baseLayer || 0) : -1;
		return alloc({ texture: t, mip: baseMip || 0, layer: layer });
	}

	function writeTexture(texture, mip, x, y, width, height, bytesPerRow, bytes) {
		var t = get(texture);
		if (!t || !t.gl) return;
		var f = t.format;
		var data = bytesOf(bytes);
		gl.bindTexture(gl.TEXTURE_2D, t.gl);
		units.touched(t.gl);

		if (f.compressed) {
			//Written in whole blocks, as WebGPU wants; GL wants the level's own size where it is smaller than a block.
			width = Math.min(width, Math.max(1, t.width >> mip) - x);
			height = Math.min(height, Math.max(1, t.height >> mip) - y);
			gl.compressedTexSubImage2D(gl.TEXTURE_2D, mip, x, y, width, height, f.internal, data);
			return;
		}

		//Half floats are read as sixteen bit words, and GL takes them only so.
		if (f.type === gl.HALF_FLOAT) data = new Uint16Array(data.buffer, data.byteOffset, data.byteLength >> 1);

		//Shared-exponent light uploads as 32-bit words; a view needs four-byte alignment, a copy does not
		if (f.type === gl.UNSIGNED_INT_5_9_9_9_REV)
			data = data.byteOffset % 4 === 0 ? new Uint32Array(data.buffer, data.byteOffset, data.byteLength >> 2) : new Uint32Array(data.slice().buffer);

		gl.pixelStorei(gl.UNPACK_ROW_LENGTH, bytesPerRow / f.texel);
		gl.texSubImage2D(gl.TEXTURE_2D, mip, x, y, width, height, f.format, f.type, data);
		gl.pixelStorei(gl.UNPACK_ROW_LENGTH, 0);
	}

	var addressModes = null;
	var filters = null;
	var compares = null;

	function createSampler(json) {
		var desc = JSON.parse(json);

		addressModes = addressModes || {
			"repeat": gl.REPEAT, "clamp-to-edge": gl.CLAMP_TO_EDGE, "mirror-repeat": gl.MIRRORED_REPEAT
		};
		compares = compares || {
			"never": gl.NEVER, "less": gl.LESS, "equal": gl.EQUAL, "less-equal": gl.LEQUAL,
			"greater": gl.GREATER, "not-equal": gl.NOTEQUAL, "greater-equal": gl.GEQUAL, "always": gl.ALWAYS
		};

		var linear = function (name) { return name === "linear"; };
		var mag = linear(desc.magFilter) ? gl.LINEAR : gl.NEAREST;
		var min = linear(desc.minFilter)
			? (linear(desc.mipmapFilter) ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR_MIPMAP_NEAREST)
			: (linear(desc.mipmapFilter) ? gl.NEAREST_MIPMAP_LINEAR : gl.NEAREST_MIPMAP_NEAREST);

		var s = gl.createSampler();
		gl.samplerParameteri(s, gl.TEXTURE_WRAP_S, addressModes[desc.addressModeU] || gl.CLAMP_TO_EDGE);
		gl.samplerParameteri(s, gl.TEXTURE_WRAP_T, addressModes[desc.addressModeV] || gl.CLAMP_TO_EDGE);
		gl.samplerParameteri(s, gl.TEXTURE_MAG_FILTER, mag);
		gl.samplerParameteri(s, gl.TEXTURE_MIN_FILTER, min);

		if (desc.compare) {
			gl.samplerParameteri(s, gl.TEXTURE_COMPARE_MODE, gl.COMPARE_REF_TO_TEXTURE);
			gl.samplerParameteri(s, gl.TEXTURE_COMPARE_FUNC, compares[desc.compare] || gl.LEQUAL);
		}

		return alloc({ gl: s, compares: !!desc.compare, destroy: function () { gl.deleteSampler(s); } });
	}

	//--- layouts and groups --- A layout says which buffers take a draw's offset; a group resolves what it
	//names by binding now, plus the one sampler every group here uses

	function createBindGroupLayout(json) {
		var desc = JSON.parse(json);
		var dynamic = [];
		for (var e of desc.entries) {
			if (e.buffer && e.buffer.hasDynamicOffset) dynamic.push(e.binding);
		}
		dynamic.sort(function (a, b) { return a - b; });
		return alloc({ dynamic: dynamic });
	}

	function createPipelineLayout(json) {
		return alloc({ layout: true });
	}

	function createBindGroup(json) {
		var desc = JSON.parse(json);
		var layout = get(desc.layout);
		//At most two samplers: depth textures use the comparison one, everything else the plain one
		var group = { entries: [], dynamic: layout ? layout.dynamic : [], sampler: null, compare: null };

		for (var e of desc.entries) {
			if (e.buffer !== undefined) group.entries[e.binding] = { buffer: get(e.buffer), offset: e.offset || 0, size: e.size || 0 };
			else if (e.textureView !== undefined) group.entries[e.binding] = { view: get(e.textureView) };
			else if (e.sampler !== undefined) {
				var s = get(e.sampler);
				group.entries[e.binding] = { sampler: s };
				if (s && s.compares) { if (!group.compare) group.compare = s; }
				else if (!group.sampler) group.sampler = s;
			}
		}

		if (!group.sampler) group.sampler = group.compare;
		if (!group.compare) group.compare = group.sampler;

		return alloc(group);
	}

	//--- shaders and pipelines ----------------------------------------------------

	//Compiled when a pipeline says which stage it is: the text alone does not.
	function createShaderModule(text, label) {
		var module = {
			text: text,
			log: "",
			shaders: {},
			destroy: function () {
				for (var kind in module.shaders) gl.deleteShader(module.shaders[kind]);
				module.shaders = {};
			}
		};
		return alloc(module);
	}

	function compile(module, kind) {
		if (module.shaders[kind]) return module.shaders[kind];
		var shader = gl.createShader(kind);
		gl.shaderSource(shader, module.text);
		gl.compileShader(shader);
		if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
			module.log = gl.getShaderInfoLog(shader) || "the shader would not compile";
			gl.deleteShader(shader);
			return null;
		}
		module.shaders[kind] = shader;
		return shader;
	}

	async function shaderMessages(module) {
		var m = get(module);
		return m ? m.log : "no module";
	}

	//A depth-only pipeline has no fragment stage in WebGPU; GL wants one.
	var emptyFragment = null;

	//The vertex formats the backend names, as attribute pointers.
	var vertexFormats = null;

	function vertexFormat(name) {
		vertexFormats = vertexFormats || {
			"float32": [1, gl.FLOAT, false, false],
			"float32x2": [2, gl.FLOAT, false, false],
			"float32x3": [3, gl.FLOAT, false, false],
			"float32x4": [4, gl.FLOAT, false, false],
			"unorm8x4": [4, gl.UNSIGNED_BYTE, true, false],
			"uint8x4": [4, gl.UNSIGNED_BYTE, false, true],
			"uint32": [1, gl.UNSIGNED_INT, false, true],
			"uint32x2": [2, gl.UNSIGNED_INT, false, true],
			"uint32x4": [4, gl.UNSIGNED_INT, false, true],
			"sint32": [1, gl.INT, false, true]
		};
		return vertexFormats[name];
	}

	var blendFactors = null;

	function blendFactor(name) {
		blendFactors = blendFactors || {
			"zero": gl.ZERO, "one": gl.ONE,
			"src": gl.SRC_COLOR, "one-minus-src": gl.ONE_MINUS_SRC_COLOR,
			"src-alpha": gl.SRC_ALPHA, "one-minus-src-alpha": gl.ONE_MINUS_SRC_ALPHA,
			"dst": gl.DST_COLOR, "one-minus-dst": gl.ONE_MINUS_DST_COLOR,
			"dst-alpha": gl.DST_ALPHA, "one-minus-dst-alpha": gl.ONE_MINUS_DST_ALPHA
		};
		return blendFactors[name] !== undefined ? blendFactors[name] : gl.ONE;
	}

	function blendOperation(name) {
		switch (name) {
			case "subtract": return gl.FUNC_SUBTRACT;
			case "reverse-subtract": return gl.FUNC_REVERSE_SUBTRACT;
			case "min": return gl.MIN;
			case "max": return gl.MAX;
			default: return gl.FUNC_ADD;
		}
	}

	function depthFunction(name) {
		switch (name) {
			case "never": return gl.NEVER;
			case "less": return gl.LESS;
			case "equal": return gl.EQUAL;
			case "less-equal": return gl.LEQUAL;
			case "greater": return gl.GREATER;
			case "not-equal": return gl.NOTEQUAL;
			case "greater-equal": return gl.GEQUAL;
			default: return gl.ALWAYS;
		}
	}

	//The program, its inputs and the fixed state round it, linked at once: WebGPU's promise resolves next turn anyway
	async function createRenderPipeline(json) {
		var desc = JSON.parse(json);
		var vertexModule = get(desc.vertexModule);
		var fragmentModule = desc.fragment ? get(desc.fragmentModule) : null;

		var vs = vertexModule ? compile(vertexModule, gl.VERTEX_SHADER) : null;
		var fs = null;

		if (fragmentModule) fs = compile(fragmentModule, gl.FRAGMENT_SHADER);
		else {
			emptyFragment = emptyFragment || compile({ text: "#version 300 es\nvoid main() {}\n", shaders: {} }, gl.FRAGMENT_SHADER);
			fs = emptyFragment;
		}

		if (!vs || !fs) {
			fail((vertexModule && vertexModule.log) || (fragmentModule && fragmentModule.log) || "a stage would not compile");
			return 0;
		}

		var program = gl.createProgram();
		gl.attachShader(program, vs);
		gl.attachShader(program, fs);
		gl.linkProgram(program);

		if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
			fail(gl.getProgramInfoLog(program) || "the program would not link");
			gl.deleteProgram(program);
			return 0;
		}

		var pipeline = {
			program: program,
			blocks: [],
			samplers: [],
			buffers: [],
			state: null,
			destroy: function () { gl.deleteProgram(program); }
		};

		//The blocks, each at a binding point of its own: group times eight plus binding, which no two of them share.
		var blockCount = gl.getProgramParameter(program, gl.ACTIVE_UNIFORM_BLOCKS);

		//Gather the points ordinary blocks take first: an array's second element needs one nothing else wants, and
		//picking it blind can pass MAX_UNIFORM_BUFFER_BINDINGS, which ES 3.0 only promises to be 24
		var taken = {};

		for (var b = 0; b < blockCount; b++) {
			var first = /^B(\d+)_(\d+)(?:\[(\d+)\])?$/.exec(gl.getActiveUniformBlockName(program, b));
			if (first && (!first[3] || +first[3] === 0)) taken[+first[1] * 8 + +first[2]] = true;
		}

		var points = gl.getParameter(gl.MAX_UNIFORM_BUFFER_BINDINGS);
		var spare = 0;

		function freePoint() {
			while (spare < points && taken[spare]) spare++;
			if (spare >= points) { fail("no uniform buffer binding left for an array of blocks"); return 0; }
			taken[spare] = true;
			return spare;
		}

		for (var i = 0; i < blockCount; i++) {
			//The frame's block is an array - of one ordinarily, of two where both of a headset's eyes
			//are drawn at once - and WebGL reports each element separately, B1_1[0] and B1_1[1].
			var name = /^B(\d+)_(\d+)(?:\[(\d+)\])?$/.exec(gl.getActiveUniformBlockName(program, i));
			if (!name) {
				fail("a uniform block not named for its binding: " + gl.getActiveUniformBlockName(program, i));
				continue;
			}

			//Each element at a point of its own. Nought keeps the point the block always had, so
			//nothing that was bound before moves; the rest take whatever this program has left.
			var group = +name[1], binding = +name[2], element = name[3] ? +name[3] : 0;
			var point = element === 0 ? group * 8 + binding : freePoint();
			gl.uniformBlockBinding(program, i, point);
			pipeline.blocks.push({
				group: group, binding: binding, element: element, point: point,
				size: gl.getActiveUniformBlockParameter(program, i, gl.UNIFORM_BLOCK_DATA_SIZE)
			});
		}

		//The samplers, each at a texture unit of its own, in the order found.
		gl.useProgram(program);
		current.program = program;

		var uniformCount = gl.getProgramParameter(program, gl.ACTIVE_UNIFORMS);
		for (var u = 0; u < uniformCount; u++) {
			var info = gl.getActiveUniform(program, u);
			var sampler = /^T(\d+)_(\d+)/.exec(info.name);
			if (!sampler) continue;
			var unit = pipeline.samplers.length;
			gl.uniform1i(gl.getUniformLocation(program, info.name), unit);
			pipeline.samplers.push({ group: +sampler[1], binding: +sampler[2], unit: unit });
		}

		for (var b of desc.vertex.buffers || []) {
			var attributes = [];
			for (var a of b.attributes) {
				var f = vertexFormat(a.format);
				if (!f) {
					fail("a vertex format WebGL2 is not told about: " + a.format);
					continue;
				}
				attributes.push({ location: a.shaderLocation, offset: a.offset, size: f[0], type: f[1], normalized: f[2], integer: f[3] });
			}
			pipeline.buffers.push({ stride: b.arrayStride, attributes: attributes });
		}

		var primitive = desc.primitive || {};
		var depth = desc.depthStencil || null;
		var target = desc.fragment && desc.fragment.targets ? desc.fragment.targets[0] : null;
		var blend = target && target.blend ? target.blend : null;

		pipeline.mode = primitive.topology === "line-list" ? gl.LINES
			: primitive.topology === "line-strip" ? gl.LINE_STRIP
			: primitive.topology === "point-list" ? gl.POINTS
			: primitive.topology === "triangle-strip" ? gl.TRIANGLE_STRIP
			: gl.TRIANGLES;

		pipeline.state = {
			cull: primitive.cullMode === "back" ? gl.BACK : primitive.cullMode === "front" ? gl.FRONT : 0,
			front: primitive.frontFace === "cw" ? gl.CW : gl.CCW,
			depthWrite: !!(depth && depth.depthWriteEnabled),
			depthFunc: depth ? depthFunction(depth.depthCompare) : gl.ALWAYS,
			bias: depth && (depth.depthBias || depth.depthBiasSlopeScale) ? [depth.depthBiasSlopeScale || 0, depth.depthBias || 0] : null,
			blend: blend ? [
				blendOperation(blend.color.operation), blendOperation(blend.alpha.operation),
				blendFactor(blend.color.srcFactor), blendFactor(blend.color.dstFactor),
				blendFactor(blend.alpha.srcFactor), blendFactor(blend.alpha.dstFactor)
			] : null,
			mask: target && target.writeMask !== undefined ? target.writeMask : 15
		};

		return alloc(pipeline);
	}

	//--- state ------------------------------------------------------------------ What GL has set now, so a draw
	//changes only what differs. Everything a pass or a clear touches behind a pipeline's back is put back through here.

	var current = null;

	function defaultState() {
		current = {
			program: null,
			cull: -1, front: -1, depthWrite: null, depthFunc: -1, bias: undefined, blend: undefined, mask: -1,
			points: [],
			enabled: [],
			attributes: []
		};
		boundArray = null;
		boundElements = null;
		gl.enable(gl.DEPTH_TEST);
		gl.enable(gl.SCISSOR_TEST);
	}

	function same(a, b) {
		if (a === b) return true;
		if (!a || !b || a.length !== b.length) return false;
		for (var i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
		return true;
	}

	function applyState(s) {
		if (current.cull !== s.cull) {
			if (s.cull) { gl.enable(gl.CULL_FACE); gl.cullFace(s.cull); }
			else gl.disable(gl.CULL_FACE);
			current.cull = s.cull;
		}
		if (current.front !== s.front) {
			gl.frontFace(s.front);
			current.front = s.front;
		}
		if (current.depthWrite !== s.depthWrite) {
			gl.depthMask(s.depthWrite);
			current.depthWrite = s.depthWrite;
		}
		if (current.depthFunc !== s.depthFunc) {
			gl.depthFunc(s.depthFunc);
			current.depthFunc = s.depthFunc;
		}
		if (!same(current.bias, s.bias)) {
			if (s.bias) { gl.enable(gl.POLYGON_OFFSET_FILL); gl.polygonOffset(s.bias[0], s.bias[1]); }
			else gl.disable(gl.POLYGON_OFFSET_FILL);
			current.bias = s.bias;
		}
		if (!same(current.blend, s.blend)) {
			if (s.blend) {
				gl.enable(gl.BLEND);
				gl.blendEquationSeparate(s.blend[0], s.blend[1]);
				gl.blendFuncSeparate(s.blend[2], s.blend[3], s.blend[4], s.blend[5]);
			} else gl.disable(gl.BLEND);
			current.blend = s.blend;
		}
		if (current.mask !== s.mask) {
			gl.colorMask(!!(s.mask & 1), !!(s.mask & 2), !!(s.mask & 4), !!(s.mask & 8));
			current.mask = s.mask;
		}
	}

	//Which texture each unit holds, and with which sampler, so a draw binds only what changed. Binding
	//a texture to make it or write it puts it in whichever unit is active, so that is said here too.
	var units = {
		textures: [],
		samplers: [],
		active: 0,
		touched: function (texture) {
			units.textures[units.active] = texture;
		},
		drop: function (texture) {
			for (var i = 0; i < units.textures.length; i++) if (units.textures[i] === texture) units.textures[i] = null;
		}
	};

	//--- passes -------------------------------------------------------------------

	var framebuffers = new Map();

	function framebufferKey(colours, depth) {
		var key = "";
		for (var c of colours) key += (c ? idOf(c.texture) + ":" + c.mip : "-") + ",";
		return key + (depth ? idOf(depth.texture) + ":" + depth.mip + ":" + depth.layer : "-");
	}

	var nextId = 1;

	function idOf(texture) {
		return texture.id || (texture.id = nextId++);
	}

	function forgetFramebuffer(key) {
		var fb = framebuffers.get(key);
		if (!fb) return;
		gl.deleteFramebuffer(fb);
		framebuffers.delete(key);
	}

	//A framebuffer over these views, made once and kept until one of them goes.
	function framebufferOf(colours, depth) {
		var key = framebufferKey(colours, depth);
		var fb = framebuffers.get(key);

		if (fb) {
			//XR layer framebuffers are kept and their colour reattached each frame; status checks are costly
			if (fb.relink && fb.relinked !== xrGeneration) {
				gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
				for (var r = 0; r < fb.relink.length; r++) attachMultiview(fb.relink[r].point, fb.relink[r].view);
				fb.relinked = xrGeneration;
			}
			return fb;
		}

		fb = gl.createFramebuffer();
		gl.bindFramebuffer(gl.FRAMEBUFFER, fb);

		var buffers = [];
		for (var i = 0; i < colours.length; i++) {
			var c = colours[i];
			if (!c) { buffers.push(gl.NONE); continue; }
			if (c.texture.layers > 1) attachMultiview(gl.COLOR_ATTACHMENT0 + i, c);
			if (c.texture.xrLayer) {
				fb.relink = fb.relink || [];
				fb.relink.push({ point: gl.COLOR_ATTACHMENT0 + i, view: c });
				fb.relinked = xrGeneration;
			}
			else if (c.texture.renderbuffer) gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0 + i, gl.RENDERBUFFER, c.texture.renderbuffer);
			else gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0 + i, gl.TEXTURE_2D, c.texture.gl, c.mip);
			c.texture.framebuffers.push(key);
			buffers.push(gl.COLOR_ATTACHMENT0 + i);
		}
		gl.drawBuffers(buffers.length ? buffers : [gl.NONE]);

		if (depth) {
			if (depth.layer >= 0) gl.framebufferTextureLayer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, depth.texture.gl, depth.mip, depth.layer);
			else if (depth.texture.layers > 1) attachMultiview(gl.DEPTH_ATTACHMENT, depth);
			else if (depth.texture.renderbuffer) gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth.texture.renderbuffer);
			else gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.TEXTURE_2D, depth.texture.gl, depth.mip);
			depth.texture.framebuffers.push(key);
		}

		var status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);

		if (status !== gl.FRAMEBUFFER_COMPLETE) {
			var why = {
				0x8CD6: "INCOMPLETE_ATTACHMENT",
				0x8CD7: "MISSING_ATTACHMENT",
				0x8CD9: "INCOMPLETE_DIMENSIONS",
				0x8CDD: "UNSUPPORTED",
				0x8D56: "INCOMPLETE_MULTISAMPLE",
				0x9633: "INCOMPLETE_VIEW_TARGETS_OVR"
			}[status] || ("0x" + status.toString(16));

			hard("a pass's pictures are not a framebuffer WebGL can draw into: " + why +
				" - colours [" + colours.map(function (c) {
					return c ? (c.texture.layers + " layer(s) " + sizeOf(c).join("x")) : "none";
				}).join(", ") + "], depth " +
				(depth ? (depth.texture.layers + " layer(s) " + sizeOf(depth).join("x")) : "none") +
				". Every draw in it is a silent no-op.");
		}

		framebuffers.set(key, fb);
		return fb;
	}

	//OVR_multiview2, asked once; null without it, and then no layered picture is made - see multiview()
	var multiviewExt;

	function multiview() {
		if (multiviewExt === undefined) multiviewExt = gl.getExtension("OVR_multiview2") || null;
		return multiviewExt;
	}

	//Every layer of an array at once, which is what makes one draw cover both of a headset's eyes: the vertex
	//stage reads gl_ViewID_OVR and the pixel stage is handed it as a flat varying - see Eye() in frame_data.slang.
	function attachMultiview(attachment, view) {
		var ext = multiview();

		if (!ext) {
			fail("a picture of more than one layer was drawn into, and this browser has no OVR_multiview2");
			return;
		}

		ext.framebufferTextureMultiviewOVR(gl.FRAMEBUFFER, attachment, view.texture.gl,
			view.mip, 0, view.texture.layers);
	}

	function sizeOf(view) {
		var t = view.texture;
		return [Math.max(1, t.width >> view.mip), Math.max(1, t.height >> view.mip)];
	}

	//The pass being recorded: its target, its height for flipping rectangles, and its draws' state so far
	var pass = null;

	//Pass descriptions by token, so JSON is parsed once instead of every pass open
	var passDescs = [];

	function definePass(token, json) {
		passDescs[token] = JSON.parse(json);
	}

	//Everything forgotten, when the backend's own table fills and starts again.
	function forgetPasses() {
		passDescs = [];
	}

	//The framebuffer the last pass drew into with depth, which a headset eye says it is done with - see xrEyeCopy
	var lastDepthFramebuffer = null;

	function beginRenderPass(encoder, token) {
		var desc = passDescs[token];
		if (!desc) fail("pass " + token + " was opened before it was described");
		var colours = [];
		var clears = [];
		var canvasPass = false;

		for (var c of desc.colors) {
			var view = c ? get(c.view) : null;
			if (view && view.texture.canvas) canvasPass = true;
			colours.push(view);
			clears.push(c && c.load === "clear" ? c.clear || [0, 0, 0, 0] : null);
		}

		var d = desc.depth || null;
		var depthView = d ? get(d.view) : null;
		var size;

		if (canvasPass) {
			//The canvas has no depth; the backend draws into its own picture and copies that here
			if (depthView) fail("the canvas cannot be drawn into beside a depth picture");
			gl.bindFramebuffer(gl.FRAMEBUFFER, null);
			size = [canvas.width, canvas.height];
			lastDepthFramebuffer = null;
		} else {
			var fb = framebufferOf(colours, depthView);
			gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
			size = sizeOf(colours[0] || depthView);
			lastDepthFramebuffer = depthView ? fb : null;
		}

		pass = {
			width: size[0],
			height: size[1],
			pipeline: null,
			groups: [null, null, null, null],
			offsets: [null, null, null, null],
			vertices: [null, null, null, null],
			vertexOffsets: [0, 0, 0, 0],
			indices: null,
			indexOffset: 0,
			framebuffer: canvasPass ? null : fb,
			resolves: null
		};

		//A multisampled colour names its resolve picture; samples average into it when the pass closes - see endPass
		for (var r = 0; r < desc.colors.length; r++) {
			var rc = desc.colors[r];
			if (!rc || !rc.resolve || !colours[r]) continue;
			pass.resolves = pass.resolves || [];
			pass.resolves.push({ attachment: r, to: get(rc.resolve) });
		}

		gl.viewport(0, 0, size[0], size[1]);
		gl.depthRange(0, 1);
		gl.scissor(0, 0, size[0], size[1]);

		//A clear writes through the last pipeline's masks, so they are opened for it; the next pipeline resets them
		if (clears.some(function (x) { return x; }) || (d && d.depthLoad === "clear")) {
			gl.colorMask(true, true, true, true);
			current.mask = 15;

			for (var i = 0; i < clears.length; i++) {
				if (!clears[i]) continue;
				if (canvasPass) {
					gl.clearColor(clears[i][0], clears[i][1], clears[i][2], clears[i][3]);
					gl.clear(gl.COLOR_BUFFER_BIT);
				} else {
					gl.clearBufferfv(gl.COLOR, i, clears[i]);
				}
			}

			if (d && d.depthLoad === "clear" && depthView) {
				gl.depthMask(true);
				current.depthWrite = true;
				gl.clearBufferfv(gl.DEPTH, 0, [d.depthClear !== undefined ? d.depthClear : 1]);
			}
		}

		return alloc({ pass: true });
	}

	function setPipeline(passHandle, pipeline) {
		var p = get(pipeline);
		if (!pass || !p) return;
		pass.pipeline = p;
		if (current.program !== p.program) {
			gl.useProgram(p.program);
			current.program = p.program;
		}
		applyState(p.state);
	}

	function setBindGroup(passHandle, index, group, dynamicOffset) {
		if (!pass) return;
		pass.groups[index] = get(group);
		pass.offsets[index] = dynamicOffset >= 0 ? dynamicOffset : null;
	}

	function setVertexBuffer(passHandle, slot, buffer, offset, size) {
		if (!pass) return;
		pass.vertices[slot] = get(buffer);
		pass.vertexOffsets[slot] = offset || 0;
	}

	function setIndexBuffer(passHandle, buffer, format, offset, size) {
		if (!pass) return;
		pass.indices = get(buffer);
		pass.indexType = format === "uint16" ? gl.UNSIGNED_SHORT : gl.UNSIGNED_INT;
		pass.indexSize = format === "uint16" ? 2 : 4;
		pass.indexOffset = offset || 0;
	}

	//WebGPU's rectangles are from the top; GL's from the bottom.
	function setViewport(passHandle, x, y, width, height, minDepth, maxDepth) {
		if (!pass) return;
		gl.viewport(x, pass.height - y - height, width, height);
		gl.depthRange(minDepth, maxDepth);
	}

	function setScissorRect(passHandle, x, y, width, height) {
		if (!pass) return;
		gl.scissor(x, pass.height - y - height, width, height);
	}

	//Binding for the second eye's frame block; must match BrowserDevice.SecondFrameBinding
	var SecondFrameBinding = 6;

	function bindGroups(p) {
		for (var b of p.blocks) {
			var group = pass.groups[b.group];
			var entry = group ? group.entries[b.element === 0 ? b.binding : SecondFrameBinding] : null;
			if (!entry || !entry.buffer) continue;

			var glBuffer = entry.buffer.as(gl.ARRAY_BUFFER);
			if (!glBuffer) continue;

			var offset = entry.offset;
			if (pass.offsets[b.group] !== null && group.dynamic.indexOf(b.binding) >= 0) offset += pass.offsets[b.group];

			var size = entry.size || (entry.buffer.size - offset);
			var was = current.points[b.point];
			if (was && was[0] === glBuffer && was[1] === offset && was[2] === size) continue;

			gl.bindBufferRange(gl.UNIFORM_BUFFER, b.point, glBuffer, offset, size);
			current.points[b.point] = [glBuffer, offset, size];
		}

		for (var s of p.samplers) {
			var g = pass.groups[s.group];
			var e = g ? g.entries[s.binding] : null;
			if (!e || !e.view || !e.view.texture.gl) continue;

			var texture = e.view.texture.gl;
			var depth = e.view.texture.format && e.view.texture.format.depth;
			var chosen = depth ? g.compare : g.sampler;
			var sampler = chosen ? chosen.gl : null;

			if (units.textures[s.unit] !== texture) {
				if (units.active !== s.unit) {
					gl.activeTexture(gl.TEXTURE0 + s.unit);
					units.active = s.unit;
				}
				gl.bindTexture(e.view.texture.target || gl.TEXTURE_2D, texture);
				units.textures[s.unit] = texture;
			}
			if (units.samplers[s.unit] !== sampler) {
				gl.bindSampler(s.unit, sampler);
				units.samplers[s.unit] = sampler;
			}
		}
	}

	//Shared first vertex across slots, or -1; lets drawArrays skip resetting attribute pointers
	function sharedVertexShift(p) {
		var shift = -1;

		for (var slot = 0; slot < p.buffers.length; slot++) {
			var layout = p.buffers[slot];
			if (!pass.vertices[slot] || !layout.stride) continue;

			var bytes = pass.vertexOffsets[slot];
			if (bytes % layout.stride !== 0) return -1;

			var here = bytes / layout.stride;
			if (shift >= 0 && here !== shift) return -1;

			shift = here;
		}

		return shift < 0 ? 0 : shift;
	}

	//Each attribute reads the draw's buffers from its first vertex, WebGL's base vertex; fromStart leaves the
	//buffers' own offsets out, as the caller folds them in - see sharedVertexShift
	function bindVertices(p, firstVertex, fromStart) {
		var used = [];

		for (var slot = 0; slot < p.buffers.length; slot++) {
			var layout = p.buffers[slot];
			var buffer = pass.vertices[slot];
			var glBuffer = buffer ? buffer.as(gl.ARRAY_BUFFER) : null;
			if (!glBuffer) continue;

			var base = ( fromStart ? 0 : pass.vertexOffsets[slot] ) + firstVertex * layout.stride;

			for (var a of layout.attributes) {
				used[a.location] = true;
				var at = base + a.offset;
				var was = current.attributes[a.location];
				if (was && was[0] === glBuffer && was[1] === at && was[2] === layout.stride && was[3] === a) continue;

				if (boundArray !== glBuffer) {
					gl.bindBuffer(gl.ARRAY_BUFFER, glBuffer);
					boundArray = glBuffer;
				}

				if (a.integer) gl.vertexAttribIPointer(a.location, a.size, a.type, layout.stride, at);
				else gl.vertexAttribPointer(a.location, a.size, a.type, a.normalized, layout.stride, at);
				current.attributes[a.location] = [glBuffer, at, layout.stride, a];
			}
		}

		var count = Math.max(used.length, current.enabled.length);
		for (var i = 0; i < count; i++) {
			var want = !!used[i];
			if (!!current.enabled[i] === want) continue;
			if (want) gl.enableVertexAttribArray(i);
			else gl.disableVertexAttribArray(i);
			current.enabled[i] = want;
		}
	}

	function draw(passHandle, vertexCount, instanceCount, firstVertex, firstInstance) {
		frameDraws++;
		var p = pass && pass.pipeline;
		if (!p) return;
		bindGroups(p);

		var shift = sharedVertexShift(p);
		var first = firstVertex;

		if (shift >= 0) { bindVertices(p, 0, true); first += shift; }
		else bindVertices(p, 0);

		if (instanceCount > 1) gl.drawArraysInstanced(p.mode, first, vertexCount, instanceCount);
		else gl.drawArrays(p.mode, first, vertexCount);
	}

	function drawIndexed(passHandle, indexCount, instanceCount, firstIndex, baseVertex, firstInstance) {
		frameDraws++;
		var p = pass && pass.pipeline;
		if (!p || !pass.indices) return;
		var indices = pass.indices.as(gl.ELEMENT_ARRAY_BUFFER);
		if (!indices) return;
		bindGroups(p);
		bindVertices(p, baseVertex);
		if (boundElements !== indices) {
			gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indices);
			boundElements = indices;
		}
		var at = pass.indexOffset + firstIndex * pass.indexSize;
		if (instanceCount > 1) gl.drawElementsInstanced(p.mode, indexCount, pass.indexType, at, instanceCount);
		else gl.drawElements(p.mode, indexCount, pass.indexType, at);
	}

	//The depth under the scissor back to the far plane - a page of a shadow map baked again, see BrowserSwapchain.ShadowArea
	function clearDepth() {
		if (!pass) return;
		gl.depthMask(true);
		current.depthWrite = true;
		gl.clearBufferfv(gl.DEPTH, 0, [1]);
	}

	function endPass(passHandle) {
		if (pass && pass.resolves) resolvePass(pass);
		pass = null;
		release(passHandle);
	}

	//The frame's commands in one call, the words webgpu.js plays (see BrowserNative's stream), each to its function
	function runPass(passHandle, bytes, count, end) {
		if (count > 0) {
			var v = bytesOf(bytes);
			var w = new Int32Array(v.buffer, v.byteOffset, count);
			var f = new Float32Array(v.buffer, v.byteOffset, count);
			var i = 0;
			while (i < count) {
				switch (w[i]) {
					case 1: setPipeline(passHandle, w[i + 1]); i += 2; break;
					case 2: setBindGroup(passHandle, w[i + 1], w[i + 2], w[i + 3]); i += 4; break;
					case 3: setVertexBuffer(passHandle, w[i + 1], w[i + 2], w[i + 3] >>> 0, w[i + 4] >>> 0); i += 5; break;
					case 4: setIndexBuffer(passHandle, w[i + 1], w[i + 2] ? "uint32" : "uint16", w[i + 3] >>> 0, w[i + 4] >>> 0); i += 5; break;
					case 5: setViewport(passHandle, f[i + 1], f[i + 2], f[i + 3], f[i + 4], f[i + 5], f[i + 6]); i += 7; break;
					case 6: setScissorRect(passHandle, w[i + 1], w[i + 2], w[i + 3], w[i + 4]); i += 5; break;
					case 7: draw(passHandle, w[i + 1], w[i + 2], w[i + 3], w[i + 4]); i += 5; break;
					case 8: drawIndexed(passHandle, w[i + 1], w[i + 2], w[i + 3], w[i + 4], w[i + 5]); i += 6; break;
					case 10: clearDepth(); i += 1; break;
					case 9: {
						var b = get(w[i + 1]), size = w[i + 3];
						if (b) b.write(w[i + 2] >>> 0, v.subarray((i + 4) * 4, (i + 4) * 4 + size));
						i += 4 + ((size + 3) >> 2); break;
					}
					default: i = count; break;
				}
			}
		}
		if (end) endPass(passHandle);
	}

	//Multisample resolve as WebGPU's resolveTarget, by a same-format blit; the scissor goes first, as it clips blits
	function resolvePass(p) {
		gl.disable(gl.SCISSOR_TEST);

		for (var i = 0; i < p.resolves.length; i++) {
			var one = p.resolves[i];
			var draw = framebufferOf([one.to], null);
			gl.bindFramebuffer(gl.READ_FRAMEBUFFER, p.framebuffer);
			gl.readBuffer(gl.COLOR_ATTACHMENT0 + one.attachment);
			gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, draw);
			gl.blitFramebuffer(0, 0, p.width, p.height, 0, 0, p.width, p.height, gl.COLOR_BUFFER_BIT, gl.NEAREST);
		}

		gl.enable(gl.SCISSOR_TEST);
		gl.bindFramebuffer(gl.FRAMEBUFFER, null);
	}

	//Copies the final sRGB image to the canvas byte-for-byte via texelFetch; a GL blit would decode it
	var present = null;

	function presentProgram() {
		if (present) return present;
		var vs = compile({ text: "#version 300 es\nvoid main() { vec2 p = vec2( float( ( gl_VertexID << 1 ) & 2 ), float( gl_VertexID & 2 ) ); gl_Position = vec4( p * 2.0 - 1.0, 0.0, 1.0 ); }\n", shaders: {} }, gl.VERTEX_SHADER);
		var fs = compile({ text: "#version 300 es\nprecision highp float;\nuniform highp sampler2D picture;\nuniform ivec2 offset;\nout vec4 colour;\n" +
			"vec3 encode( vec3 c ) { return mix( c * 12.92, 1.055 * pow( c, vec3( 1.0 / 2.4 ) ) - 0.055, step( vec3( 0.0031308 ), c ) ); }\n" +
			"void main() { colour = vec4( encode( texelFetch( picture, ivec2( gl_FragCoord.xy ) + offset, 0 ).rgb ), 1.0 ); }\n", shaders: {} }, gl.FRAGMENT_SHADER);
		var program = gl.createProgram();
		gl.attachShader(program, vs);
		gl.attachShader(program, fs);
		gl.linkProgram(program);
		present = { program: program, picture: gl.getUniformLocation(program, "picture"), offset: gl.getUniformLocation(program, "offset") };
		return present;
	}

	function presentToCanvas(from, fromSize, width, height) {
		//The top rows of both, in GL's rows from the bottom.
		presentInto(from, fromSize, null, 0, canvas.height - height, width, height);
	}

	//Present into a framebuffer rectangle; array variant reads one eye layer of a 2D array
	var presentArray = null;

	function presentArrayProgram() {
		if (presentArray) return presentArray;

		var vs = compile({ text: "#version 300 es\nvoid main() { vec2 p = vec2( float( ( gl_VertexID << 1 ) & 2 ), float( gl_VertexID & 2 ) ); gl_Position = vec4( p * 2.0 - 1.0, 0.0, 1.0 ); }\n", shaders: {} }, gl.VERTEX_SHADER);
		var fs = compile({ text: "#version 300 es\nprecision highp float;\nuniform highp sampler2DArray picture;\nuniform ivec2 offset;\nuniform int layer;\nout vec4 colour;\n" +
			"vec3 encode( vec3 c ) { return mix( c * 12.92, 1.055 * pow( c, vec3( 1.0 / 2.4 ) ) - 0.055, step( vec3( 0.0031308 ), c ) ); }\n" +
			"void main() { colour = vec4( encode( texelFetch( picture, ivec3( ivec2( gl_FragCoord.xy ) + offset, layer ), 0 ).rgb ), 1.0 ); }\n", shaders: {} }, gl.FRAGMENT_SHADER);

		var program = gl.createProgram();
		gl.attachShader(program, vs);
		gl.attachShader(program, fs);
		gl.linkProgram(program);

		presentArray = {
			program: program,
			picture: gl.getUniformLocation(program, "picture"),
			offset: gl.getUniformLocation(program, "offset"),
			layer: gl.getUniformLocation(program, "layer")
		};

		return presentArray;
	}

	function presentInto(from, fromSize, framebuffer, x, y, width, height, layer) {
		var layered = from.layers > 1;
		var p = layered ? presentArrayProgram() : presentProgram();

		gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
		gl.disable(gl.SCISSOR_TEST);
		gl.disable(gl.BLEND);
		gl.colorMask(true, true, true, true);

		//Depth test is on throughout (defaultState) and a headset framebuffer's depth is never cleared, so set it here
		gl.depthFunc(gl.ALWAYS);
		gl.depthMask(false);

		gl.useProgram(p.program);

		//Nothing read from a buffer: the three corners come from gl_VertexID.
		for (var i = 0; i < current.enabled.length; i++) {
			if (current.enabled[i]) gl.disableVertexAttribArray(i);
			current.enabled[i] = false;
		}

		if (units.active !== 0) { gl.activeTexture(gl.TEXTURE0); units.active = 0; }
		gl.bindTexture(from.target || gl.TEXTURE_2D, from.gl);
		gl.bindSampler(0, null);
		units.textures[0] = from.gl;
		units.samplers[0] = null;

		gl.viewport(x, y, width, height);
		gl.uniform1i(p.picture, 0);
		gl.uniform2i(p.offset, -x, fromSize[1] - height - y);
		if (layered) gl.uniform1i(p.layer, layer || 0);
		gl.drawArrays(gl.TRIANGLES, 0, 3);

		gl.enable(gl.SCISSOR_TEST);

		//What the state was left as, so the next pipeline notices and sets its own.
		current.program = p.program;
		current.blend = null;
		current.mask = 15;
		current.depthFunc = gl.ALWAYS;
		current.depthWrite = false;
	}

	//A picture-to-picture copy is a blit, top to top: in GL's bottom-up rows, the last rows of each
	function copyTextureToTexture(encoder, source, sourceMip, destination, destinationMip, width, height) {
		var from = get(source);
		var to = get(destination);
		if (!from || !to || !from.gl) return;

		var fromView = { texture: from, mip: sourceMip };
		var fromSize = sizeOf(fromView);

		if (to.canvas) {
			presentToCanvas(from, fromSize, width, height);
			return;
		}

		var toView = { texture: to, mip: destinationMip };
		var toHeight = sizeOf(toView)[1];

		//Both made before either is bound: making one binds it to both targets.
		var read = framebufferOf([fromView], null);
		var draw = framebufferOf([toView], null);
		gl.bindFramebuffer(gl.READ_FRAMEBUFFER, read);
		gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, draw);

		gl.disable(gl.SCISSOR_TEST);
		gl.blitFramebuffer(0, fromSize[1] - height, width, fromSize[1], 0, toHeight - height, width, toHeight,
			gl.COLOR_BUFFER_BIT, gl.NEAREST);
		gl.enable(gl.SCISSOR_TEST);
		gl.bindFramebuffer(gl.FRAMEBUFFER, null);
	}

	//--- recording ------------------------------------------------------------------

	function createCommandEncoder() {
		gpuBegin();
		return alloc({ encoder: true });
	}

	//With validation on, GL's errors are asked once a frame; after every call would cost the frame
	function finishAndSubmit(encoder) {
		gpuEnd();
		release(encoder);
		if (!validation) return;
		var error = gl.getError();
		if (error !== gl.NO_ERROR) fail("GL error " + error + " during the frame");
	}

	//--- diagnostics --------------------------------------------------------------

	//GPU frame time via timer-query timestamps (TIME_ELAPSED breaks multiview); -1 if unsupported
	var timerExt;
	var gpuWanted = false;
	var gpuOpen = null;
	var gpuPending = [];
	var gpuMs = 0;
	var frameDraws = 0;

	function timer() {
		if (timerExt === undefined) {
			timerExt = gl.getExtension("EXT_disjoint_timer_query_webgl2") || null;

			//Some GPUs have the extension and no clock to stamp with.
			if (timerExt && !(timerExt.queryCounterEXT && gl.getQuery(timerExt.TIMESTAMP_EXT, timerExt.QUERY_COUNTER_BITS_EXT) > 0))
				timerExt = null;
		}
		return timerExt;
	}

	function stamp() {
		var q = gl.createQuery();
		timerExt.queryCounterEXT(q, timerExt.TIMESTAMP_EXT);
		return q;
	}

	function gpuBegin() {
		if (!gpuWanted || gpuOpen || !timer()) return;
		gpuOpen = stamp();
	}

	function gpuEnd() {
		if (!gpuOpen) return;
		gpuPending.push([gpuOpen, stamp()]);
		gpuOpen = null;
	}

	//GPU milliseconds finished since the last ask, about a frame's, or -1 without a timer; want keeps timing on
	function gpuTake(want) {
		gpuWanted = !!want;
		if (!timer()) return -1;

		var disjoint = gl.getParameter(timerExt.GPU_DISJOINT_EXT);

		while (gpuPending.length) {
			var pair = gpuPending[0];
			if (!gl.getQueryParameter(pair[1], gl.QUERY_RESULT_AVAILABLE)) break;
			var from = gl.getQueryParameter(pair[0], gl.QUERY_RESULT);
			var to = gl.getQueryParameter(pair[1], gl.QUERY_RESULT);
			gpuPending.shift();
			gl.deleteQuery(pair[0]);
			gl.deleteQuery(pair[1]);
			if (!disjoint && to > from) gpuMs += (to - from) / 1e6;
		}

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

	function popError() {
		var text = lastError;
		lastError = "";
		return text;
	}

	//--- the headset --- WebXR is WebGL2-only here; webxr.js owns the session, this owns the layer

	var xrLayer = null;
	var xrViewports = [null, null];

	//WebXR Layers projection layer: engine renders both eyes straight into it, no copy
	var xrBinding = null;
	var xrProjection = null;
	var xrSubs = [null, null];

	//The engine's eye picture once the first copy names it, and what it was before pointing at the layer - see xrAlias
	var xrSource = 0;
	var xrOwn = null;
	var xrGeneration = 0;

	//Eye resolution and how much edge is shaded coarsely; nought is the headset's own one-to-one. Set by xrTune,
	//overridden by ?xrscale and ?xrfoveation on the address for a look without a rebuild
	var xrScale = 0;
	var xrFoveation = 1;

	//Said when the lean layer was refused, so the page can report it.
	var xrFallback = "";

	//Headset errors kept separate from fail() so webxr.js can show them to the player
	var xrError = "";

	function xrFail(text) {
		xrError = text;
		if (globalThis.console) console.warn("WebXR: " + text);
	}

	//The address overrides the engine, so a build can be viewed at half the pixels; below nought is unset
	var xrForced = null;

	function forced(name) {
		if (!xrForced) {
			var query = new URLSearchParams(location.search);
			xrForced = {
				xrscale: query.has("xrscale") ? parseFloat(query.get("xrscale")) : -1,
				xrfoveation: query.has("xrfoveation") ? parseFloat(query.get("xrfoveation")) : -1,
				//?xrnolayers: the XRWebGLLayer and the copy, as before, for comparing.
				xrnolayers: query.has("xrnolayers") ? 1 : -1
			};
		}

		var value = xrForced[name];
		return isFinite(value) && value >= 0 ? value : -1;
	}

	function wantedScale() {
		var said = forced("xrscale");
		return said >= 0 ? said : xrScale;
	}

	function wantedFoveation() {
		var said = forced("xrfoveation");
		return said >= 0 ? Math.min(1, said) : xrFoveation;
	}

	function xrCan() { return gl ? 1 : 0; }

	//Whether to ask for "layers", decided before the session opens (it then refuses a baseLayer); only where
	//the layer takes the engine's drawing: a texture array through OVR_multiview2
	function xrLayersWanted() {
		return gl && typeof XRWebGLBinding === "function" && multiview() && forced("xrnolayers") < 0 ? 1 : 0;
	}

	//Native framebuffer scale; the browser default is below panel resolution
	var xrNativeScale = 0;

	function xrNative() { return xrNativeScale; }

	//Before a session opens, as the layer's size is fixed when made; foveation takes hold whenever set
	function xrTune(scale, foveation) {
		xrScale = scale > 0 ? scale : 0;
		xrFoveation = Math.max(0, Math.min(1, foveation));

		applyFoveation();
	}

	function applyFoveation() {
		var layer = xrProjection || xrLayer;
		if (!layer) return;

		//A headset without fixed foveation refuses the write; nothing to fall back to
		try { layer.fixedFoveation = wantedFoveation(); } catch (e) {}
	}

	//The projection layer or nothing: sRGB, as the engine's picture is, so the bytes reaching the eye are the
	//copy's; no depth, as the engine tests against its own
	function projectionLayer(session) {
		if (!session.enabledFeatures || session.enabledFeatures.indexOf("layers") < 0) return false;

		try {
			xrBinding = new XRWebGLBinding(session, gl);

			var wanted = { textureType: "texture-array", colorFormat: gl.SRGB8_ALPHA8, depthFormat: 0 };
			var scale = wantedScale();
			if (scale > 0) wanted.scaleFactor = scale;

			xrProjection = xrBinding.createProjectionLayer(wanted);
			return true;
		} catch (e) {
			xrFallback = "the projection layer was refused (" +
				((e && e.name) ? e.name : String(e)) + ") - drawing through an XRWebGLLayer";
			xrBinding = null;
			xrProjection = null;
			return false;
		}
	}

	//Lean layer (no depth, stencil, alpha or MSAA) with the browser default as fallback
	function layerOrDefault(session) {
		try {
			xrNativeScale = XRWebGLLayer.getNativeFramebufferScaleFactor(session) || 0;
		} catch (e) {
			xrNativeScale = 0;
		}

		var wanted = {
			antialias: false,
			depth: false,
			stencil: false,
			alpha: false,
			ignoreDepthValues: true
		};

		var scale = wantedScale();
		if (scale > 0) wanted.framebufferScaleFactor = scale;

		try {
			xrLayer = new XRWebGLLayer(session, gl, wanted);
			return true;
		} catch (e) {
			xrFallback = "the lean layer was refused (" +
				((e && e.name) ? e.name : String(e)) + ") - using the browser's own";
		}

		try {
			xrLayer = new XRWebGLLayer(session, gl);
			return true;
		} catch (e) {
			xrFail("XRWebGLLayer failed even at the browser's defaults: " +
				((e && e.name) ? e.name + ": " : "") + String((e && e.message) || e));
			xrLayer = null;
			return false;
		}
	}

	function xrBind(session) {
		if (!gl) {
			xrFail("there is no WebGL2 context to draw a headset from");
			return Promise.resolve(false);
		}

		xrError = "";
		xrFallback = "";

		//Best effort: context is already XR-compatible, so a rejection here must not abort
		return gl.makeXRCompatible().catch(function (e) {
			xrFallback = "makeXRCompatible refused (" +
				((e && e.name) ? e.name : String(e)) + ") - trying the layer anyway";
		}).then(function () {

			//Try the lean layer first, then fall back to the browser's default layer
			try {
				xrNativeScale = XRWebGLLayer.getNativeFramebufferScaleFactor(session) || 0;
			} catch (e) {
				xrNativeScale = 0;
			}

			if (projectionLayer(session)) return "projection";
			return layerOrDefault(session);
		}).then(function (made) {
			if (!made) return false;

			if (made === "projection") {
				applyFoveation();
				session.updateRenderState({ layers: [xrProjection] });
				return true;
			}

			//A session that granted "layers" refuses a baseLayer; an XRWebGLLayer goes in the list instead.
			var layered = session.enabledFeatures && session.enabledFeatures.indexOf("layers") >= 0;

			//All off on purpose: the eye is copied in already resolved, so the layer needs no depth or MSAA
			applyFoveation();

			session.updateRenderState(layered ? { layers: [xrLayer] } : { baseLayer: xrLayer });
			return true;
		}).catch(function (e) {
			//The browser's own words: "the layer would not be made" is four different faults wearing one coat.
			xrFail("entering the headset failed: " + ((e && e.name) ? e.name + ": " : "") +
				String((e && e.message) || e));
			xrLayer = null;
			return false;
		});
	}

	function xrDrop() {
		xrUnalias();
		xrLayer = null;
		xrProjection = null;
		xrBinding = null;
		xrSource = 0;
		xrSubs = [null, null];
		xrViewports = [null, null];
	}

	//Points the engine picture at this frame's layer texture when shapes match; it changes per frame
	function xrAlias(sub) {
		var picture = xrSource ? get(xrSource) : null;
		if (!picture || !picture.gl || !sub || !sub.colorTexture) return;
		if (picture.layers !== 2 || picture.format.internal !== gl.SRGB8_ALPHA8) return;
		if (picture.width !== xrProjection.textureWidth || picture.height !== xrProjection.textureHeight) return;

		if (!xrOwn) {
			xrOwn = { picture: picture, gl: picture.gl, id: idOf(picture) };

			//Built on the picture's own texture: dropped once so the next pass rebuilds them, marked to follow it
			for (var i = 0; i < picture.framebuffers.length; i++) forgetFramebuffer(picture.framebuffers[i]);
			picture.framebuffers.length = 0;
		}
		if (xrOwn.picture !== picture) return;

		picture.gl = sub.colorTexture;
		picture.xrLayer = true;
		xrGeneration++;
	}

	//And back, when the session ends: its textures go with it, and so do the framebuffers built on them.
	function xrUnalias() {
		if (!xrOwn) return;

		var picture = xrOwn.picture;
		for (var i = 0; i < picture.framebuffers.length; i++) forgetFramebuffer(picture.framebuffers[i]);
		picture.framebuffers.length = 0;
		units.drop(picture.gl);
		picture.gl = xrOwn.gl;
		picture.id = xrOwn.id;
		picture.xrLayer = false;

		xrOwn = null;
	}

	//This frame's views, left first, as webxr.js ordered them. A viewport is only answered for while
	//the session's animation callback runs, so it is taken here rather than when something asks for it.
	function xrViews(views) {
		if (xrProjection && views) {
			for (var i = 0; i < 2; i++) {
				var sub = null;
				try { sub = xrBinding.getViewSubImage(xrProjection, views[i]); } catch (e) { sub = null; }
				xrSubs[i] = sub;
				xrViewports[i] = sub ? sub.viewport : null;
			}

			//Both eyes are one texture array, so either names it.
			xrAlias(xrSubs[0]);
			return;
		}

		if (!xrLayer || !views) {
			xrViewports = [null, null];
			return;
		}

		for (var i = 0; i < 2; i++) xrViewports[i] = xrLayer.getViewport(views[i]) || null;
	}

	function xrSize(eye, which) {
		var vp = xrViewports[eye];
		if (!vp) return 0;
		return which === 0 ? vp.width : vp.height;
	}

	//Nought is no eye this frame; otherwise it names one, back through BrowserSwapchain.ViewCopyTo
	function xrTarget(eye) {
		return xrViewports[eye] ? eye + 1 : 0;
	}

	//A finished eye into its part of the headset framebuffer, encoded as for the canvas; the given layer where
	//the picture holds both eyes (see multiview), else all of it
	function xrEyeCopy(source, eye, layer) {
		var vp = xrViewports[eye];
		var from = get(source);
		if (!vp || !from || !from.gl) return 0;

		//The next frame clears the eye's depth, so invalidate it: a tiled GPU otherwise writes it out every pass
		if (eye === 0 && lastDepthFramebuffer) {
			gl.bindFramebuffer(gl.FRAMEBUFFER, lastDepthFramebuffer);
			gl.invalidateFramebuffer(gl.FRAMEBUFFER, [gl.DEPTH_ATTACHMENT]);
			lastDepthFramebuffer = null;
		}

		if (xrProjection) {
			var sub = xrSubs[eye];
			if (!sub) return 0;

			xrSource = source;

			//Drawn into the layer's own texture already - see xrAlias.
			if (from.gl === sub.colorTexture) return 1;

			//An eye arriving as its own picture means the frame went back to one eye at a time
			//(CoreSceneRenderer.StereoIncomplete), so it stops holding the layer's texture
			if (xrOwn && xrOwn.picture !== from) xrUnalias();

			//Fallback copy into the eye layer by blit, reading one layer of the array or the whole picture
			if (!xrCopyRead) { xrCopyRead = gl.createFramebuffer(); xrCopyDraw = gl.createFramebuffer(); }
			gl.bindFramebuffer(gl.READ_FRAMEBUFFER, xrCopyRead);
			if (from.layers > 1)
				gl.framebufferTextureLayer(gl.READ_FRAMEBUFFER, gl.COLOR_ATTACHMENT0, from.gl, 0, layer || 0);
			else
				gl.framebufferTexture2D(gl.READ_FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, from.gl, 0);
			gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, xrCopyDraw);
			gl.framebufferTextureLayer(gl.DRAW_FRAMEBUFFER, gl.COLOR_ATTACHMENT0, sub.colorTexture, 0, sub.imageIndex || 0);
			gl.disable(gl.SCISSOR_TEST);
			gl.blitFramebuffer(0, 0, Math.min(from.width, vp.width), Math.min(from.height, vp.height),
				vp.x, vp.y, vp.x + Math.min(from.width, vp.width), vp.y + Math.min(from.height, vp.height),
				gl.COLOR_BUFFER_BIT, gl.NEAREST);
			gl.enable(gl.SCISSOR_TEST);
			gl.bindFramebuffer(gl.FRAMEBUFFER, null);
			return 1;
		}

		if (!xrLayer) return 0;

		presentInto(from, sizeOf({ texture: from, mip: 0 }), xrLayer.framebuffer,
			vp.x, vp.y, vp.width, vp.height, layer);

		return 1;
	}

	var xrCopyRead = null, xrCopyDraw = null;

	//Whether this browser can draw both of a headset's eyes in one pass.
	function xrStereo() { return multiview() ? 1 : 0; }

	globalThis.RintenBackends = globalThis.RintenBackends || {};

	globalThis.RintenBackends.webgl2 = {
		init: init,
		ready: function () { return ready; },
		lost: function () { return lost; },
		info: function () { return infoText; },
		deviceName: function () { return deviceNameText; },
		language: function () { return "glsl"; },
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
		finishAndSubmit: finishAndSubmit,

		xrCan: xrCan,
		xrLayersWanted: xrLayersWanted,
		gpuTake: gpuTake,
		drawsTake: drawsTake,
		xrLayered: function () { return xrProjection ? 1 : 0; },
		xrError: function () { return xrError; },
		xrFallback: function () { return xrFallback; },
		xrTune: xrTune,
		xrNative: xrNative,
		xrBind: xrBind,
		xrDrop: xrDrop,
		xrViews: xrViews,
		xrSize: xrSize,
		xrTarget: xrTarget,
		xrEyeCopy: xrEyeCopy,
		xrStereo: xrStereo
	};
})();
