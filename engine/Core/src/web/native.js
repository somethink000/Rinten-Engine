//Page services wasm can't get itself: canvas, input, loading text, prefetched build files

function RintenNativeBind(host) {
	var canvas = (host && host.canvas) || document.getElementById("canvas");

	var pointerX = 0;
	var pointerY = 0;
	var buttons = 0;
	//Buttons that went down since the engine last asked, kept until it has seen them: a tap on a touch
	//screen is often down and up again between two frames, and a mask read once a frame never saw it at all.
	var pressed = 0;
	var deltaX = 0;
	var deltaY = 0;

	//The wheel since the engine last asked, in notches up and right as SDL counts them
	var wheelX = 0;
	var wheelY = 0;

	//What the last fetchBytes brought back, handed over once and dropped.
	var fetched = null;

	//Canvas size in device pixels from ResizeObserver, so the browser never resamples the frame
	var devicePixels = null;

	if (canvas && typeof ResizeObserver !== "undefined") {
		try {
			new ResizeObserver(function (entries) {
				var entry = entries[entries.length - 1];
				var box = entry.devicePixelContentBoxSize && entry.devicePixelContentBoxSize[0];
				devicePixels = box ? { w: box.inlineSize, h: box.blockSize } : null;
			}).observe(canvas, { box: "device-pixel-content-box" });
		} catch (err) {
			devicePixels = null;
		}
	}

	function canvasCssSize() {
		if (!canvas) return { w: 1280, h: 720 };

		if (devicePixels && devicePixels.w > 0 && devicePixels.h > 0)
			return { w: devicePixels.w, h: devicePixels.h };

		var dpr = window.devicePixelRatio || 1;
		var rect = canvas.getBoundingClientRect();
		return {
			w: Math.max(1, Math.round((rect.width || 1280) * dpr)),
			h: Math.max(1, Math.round((rect.height || 720) * dpr))
		};
	}

	function fitCanvas() {
		var size = canvasCssSize();
		if (!canvas) return size;
		if (canvas.width !== size.w) canvas.width = size.w;
		if (canvas.height !== size.h) canvas.height = size.h;
		return size;
	}

	//A build file's path as an address: each part escaped, as a name may hold "#", "?" or "%" - a "#" ended the address there
	function addressOf(path) {
		if (path.indexOf("://") >= 0) return path;
		return path.split("/").map(encodeURIComponent).join("/");
	}

	//Fetches one build file; a "+" prefix means it is shipped as .gz and inflated here. Null if missing
	function fetchFile(path) {
		var packed = path.charAt(0) === "+";
		var url = addressOf(packed ? path.slice(1) + ".gz" : path);

		return fetch(url).then(function (r) {
			if (!r.ok) return null;
			if (!packed || !r.body || (r.headers.get("Content-Encoding") || "") !== "") return r.arrayBuffer();
			return new Response(r.body.pipeThrough(new DecompressionStream("gzip"))).arrayBuffer();
		}).catch(function () {
			return null;
		});
	}

	var prefetched = null;
	var prefetchDone = 0;
	var prefetchTotal = 0;

	var keys = Object.create(null);

	//Moves whenever any key goes down or up, so the engine can ask one number a frame instead of every key it knows - see CoreInput.PumpWeb.
	var keyVersion = 0;
	var wantsPointerLock = false;

	//Keys a page would otherwise act on itself: arrows and space scroll, tab walks the focus out of
	//the canvas and into whatever is around it - which on itch.io is the page the game is embedded in.
	var swallowed = {
		ArrowUp: 1, ArrowDown: 1, ArrowLeft: 1, ArrowRight: 1,
		Space: 1, Tab: 1, Backspace: 1, F1: 1, F5: 1, Slash: 1, Quote: 1
	};

	function lockPointer(target) {
		if (!target || !target.requestPointerLock) return;
		if (document.pointerLockElement === target) return;
		try { target.requestPointerLock(); } catch (err) {}
	}

	//Fullscreen needs a gesture, so a request is remembered and taken on the next click or key
	var wantsFullscreen = false;

	function fullscreenRoot() {
		return document.documentElement || document.body;
	}

	function fullscreenElement() {
		return document.fullscreenElement || document.webkitFullscreenElement || null;
	}

	function fullscreenAllowed() {
		var root = fullscreenRoot();
		if (!root) return false;
		if (document.fullscreenEnabled === false) return false;
		return !!(root.requestFullscreen || root.webkitRequestFullscreen);
	}

	function enterFullscreen() {
		var root = fullscreenRoot();
		var ask = root && (root.requestFullscreen || root.webkitRequestFullscreen);
		if (!ask) return;

		try {
			//The address bar and the like folded away where a browser offers it; the option is ignored where it does not.
			var going = ask.call(root, { navigationUI: "hide" });
			if (going && going.catch) going.catch(function () {});
		} catch (err) {}
	}

	function leaveFullscreen() {
		var drop = document.exitFullscreen || document.webkitExitFullscreen;
		if (!drop) return;

		try {
			var going = drop.call(document);
			if (going && going.catch) going.catch(function () {});
		} catch (err) {}
	}

	//The wish, taken now that there is a gesture to take it with.
	function settleFullscreen() {
		if (wantsFullscreen && !fullscreenElement()) enterFullscreen();
	}

	function fullscreenChanged() {
		wantsFullscreen = !!fullscreenElement();
		fitCanvas();
	}

	document.addEventListener("fullscreenchange", fullscreenChanged);
	document.addEventListener("webkitfullscreenchange", fullscreenChanged);

	function bindInput(target) {
		if (!target || target._rintenInput) return;
		target._rintenInput = true;
		//Where the pointer is, in the canvas's own pixels. Taken on a press and a release as well as a move:
		//a finger arrives with no move before it, and the press used to be read where the last touch had been.
		function place(e) {
			var box = target.getBoundingClientRect();
			if (box.width <= 0 || box.height <= 0) return;
			pointerX = (e.clientX - box.left) / box.width * target.width;
			pointerY = (e.clientY - box.top) / box.height * target.height;
		}
		target.addEventListener("pointermove", function (e) {
			if (document.pointerLockElement === target) {
				deltaX += e.movementX;
				deltaY += e.movementY;
				return;
			}
			place(e);
		});
		target.addEventListener("pointerdown", function (e) {
			if (document.pointerLockElement !== target) place(e);
			buttons |= (1 << e.button);
			pressed |= (1 << e.button);
			try { target.setPointerCapture(e.pointerId); } catch (err) {}
		});
		target.addEventListener("click", function () {
			target.focus();
			if (wantsPointerLock) lockPointer(target);
			settleFullscreen();
		});
		function release(e) {
			if (document.pointerLockElement !== target) place(e);
			buttons &= ~(1 << e.button);
			try {
				if (target.hasPointerCapture && target.hasPointerCapture(e.pointerId))
					target.releasePointerCapture(e.pointerId);
			} catch (err) {}
		}
		target.addEventListener("pointerup", release);
		//As SDL's own page reads it: a notch three lines or a hundred pixels, down the page is down the wheel. Not
		//passive, so the page - an itch.io frame round the game - does not scroll under it instead
		target.addEventListener("wheel", function (e) {
			var notch = e.deltaMode === 1 ? 1 / 3 : e.deltaMode === 2 ? 1 : 1 / 100;
			wheelX += e.deltaX * notch;
			wheelY -= e.deltaY * notch;
			e.preventDefault();
		}, { passive: false });
		//A touch the browser took back - a system gesture, the page losing the finger - ends without a pointerup, and the button stayed down for ever.
		target.addEventListener("pointercancel", function (e) {
			buttons &= ~(1 << Math.max(e.button, 0));
		});
		window.addEventListener("keydown", function (e) {
			if (!keys[e.code]) keyVersion++;
			keys[e.code] = 1;
			unread[e.code] = 1;
			releaseLater[e.code] = 0;
			if (swallowed[e.code] && !e.ctrlKey && !e.metaKey) e.preventDefault();
			settleFullscreen();
		});
		window.addEventListener("blur", function () {
			keys = Object.create(null); unread = Object.create(null); releaseLater = Object.create(null);
			buttons = 0; pressed = 0; keyVersion++;
		});
		window.addEventListener("keyup", function (e) {
			//Let go before the engine saw it down - both between two of its frames - and it stays down until it has been read once
			if (unread[e.code]) releaseLater[e.code] = 1;
			else keys[e.code] = 0;
			keyVersion++;
		});
	}

	//Keys pressed that the engine has not read yet, and of those, the ones already let go - see keyDown
	var unread = Object.create(null);
	var releaseLater = Object.create(null);

	//What the engine reads a key as, once a frame: a key let go before it was read is down this once, and up from the next
	function readKey(code) {
		var down = keys[code] ? 1 : 0;
		if (!unread[code]) return down;

		unread[code] = 0;
		if (releaseLater[code]) {
			releaseLater[code] = 0;
			keys[code] = 0;
			keyVersion++;
		}

		return down;
	}

	//miniaudio wakes the audio on a click or a tap, once, and drops its listeners even where no sound had started yet - so a
	//game begun from the keyboard, or clicked while it loaded, stayed silent. Any key, pointer or touch wakes what is asleep.
	function wakeAudio() {
		var devices = globalThis.miniaudio && globalThis.miniaudio.devices;
		if (!devices) return;

		for (var i = 0; i < devices.length; i++) {
			var audio = devices[i] && devices[i].webaudio;
			if (audio && audio.state === "suspended") audio.resume().catch(function () { });
		}
	}

	for (var wake of ["keydown", "pointerdown", "click", "touchend"]) window.addEventListener(wake, wakeAudio, true);

	bindInput(canvas);
	fitCanvas();

	//Game loop driven from JS. Body returns 0 stop, 1 next task (vsync off), 2 next animation frame
	var loopBody = null;
	var loopDone = null;
	var channel = typeof MessageChannel !== "undefined" ? new MessageChannel() : null;

	function loopStep() {
		if (!loopBody) return;

		var next = 0;
		try { next = loopBody(); } catch (err) { console.error(err); next = 0; }

		if (!next) {
			loopBody = null;
			if (loopDone) loopDone();
			return;
		}

		if (next === 1 && channel && !document.hidden) channel.port2.postMessage(0);
		else requestAnimationFrame(loopStep);
	}

	if (channel) channel.port1.onmessage = loopStep;

	function runLoop(body) {
		return new Promise(function (resolve) {
			loopBody = body;
			loopDone = resolve;
			requestAnimationFrame(loopStep);
		});
	}

	return {
		canvas: canvas,
		whenReady: function () { return globalThis.nativeReady || Promise.resolve(); },
		canvasWidth: function () { return (canvas && canvas.width) || 1280; },
		canvasHeight: function () { return (canvas && canvas.height) || 720; },
		canvasCssWidth: function () { return canvasCssSize().w; },
		canvasCssHeight: function () { return canvasCssSize().h; },
		fitCanvas: function () { var size = fitCanvas(); return size.w; },
		pointerX: function () { return pointerX; },
		pointerY: function () { return pointerY; },
		pointerDeltaX: function () { var v = deltaX; deltaX = 0; return v; },
		pointerDeltaY: function () { var v = deltaY; deltaY = 0; return v; },
		wheelX: function () { var v = wheelX; wheelX = 0; return v; },
		wheelY: function () { var v = wheelY; wheelY = 0; return v; },
		pointerLocked: function () { return document.pointerLockElement === canvas ? 1 : 0; },
		setPointerLock: function (state) {
			wantsPointerLock = !!state;
			if (wantsPointerLock) lockPointer(canvas);
			else if (document.pointerLockElement === canvas) document.exitPointerLock();
		},
		fullscreen: function () { return fullscreenElement() ? 1 : 0; },
		fullscreenAllowed: function () { return fullscreenAllowed() ? 1 : 0; },
		setFullscreen: function (state) {
			wantsFullscreen = !!state;

			if (!fullscreenAllowed()) return;
			if (wantsFullscreen) enterFullscreen();
			else if (fullscreenElement()) leaveFullscreen();
		},
		//Held, or pressed since the last ask - which is the engine's frame - so a press is seen for a frame even when it was already let go.
		buttons: function () { var v = buttons | pressed; pressed = 0; return v; },
		keyDown: readKey,
		keyVersion: function () { return keyVersion; },
		nextFrame: function () {
			return new Promise(function (resolve) { requestAnimationFrame(function () { resolve(); }); });
		},
		runLoop: runLoop,
		say: function (text) {
			if (typeof globalThis.rintenSay === "function") globalThis.rintenSay(text);
		},
		progress: function (done, total) {
			if (typeof globalThis.rintenProgress === "function") globalThis.rintenProgress(done, total);
		},
		setTitle: function (text) {
			if (text) document.title = text;
		},
		fetchText: function (url) {
			return fetch(addressOf(url)).then(function (r) {
				if (!r.ok) return "";
				return r.text();
			}).catch(function () { return ""; });
		},
		fetchBytes: function (url) {
			return fetchFile(url).then(function (buf) {
				fetched = buf ? new Uint8Array(buf) : null;
				return fetched ? fetched.length : 0;
			});
		},
		takeFetch: function () {
			var data = fetched;
			fetched = null;
			return data || new Uint8Array(0);
		},
		//The same, written into the module's own memory where .NET asks - a view of a buffer it holds outside its heap.
		takeFetchInto: function (view) {
			if (fetched) view.set(fetched, 0);
			fetched = null;
		},
		prefetch: function (listText, concurrency) {
			var paths = String(listText || "").split("\n")
				.map(function (line) { return line.trim(); })
				.filter(function (line) { return line && line[0] !== "#"; });

			prefetched = new Map();
			prefetchDone = 0;
			prefetchTotal = paths.length;

			var next = 0;

			//One fetch at a time is one round trip at a time, and a game is hundreds of files - so a handful
			//of them run side by side, which is what a browser will keep in flight to one host anyway.
			function pump() {
				if (next >= paths.length) return Promise.resolve();

				var path = paths[next++];
				return fetchFile(path).then(function (buf) {
					if (buf) prefetched.set(path, new Uint8Array(buf));
					prefetchDone++;
					if (typeof globalThis.rintenProgress === "function")
						globalThis.rintenProgress(prefetchDone, prefetchTotal);
					return pump();
				});
			}

			var workers = Math.max(1, Math.min(concurrency | 0 || 8, 32));
			var running = [];
			for (var i = 0; i < workers; i++) running.push(pump());

			return Promise.all(running).then(function () { return prefetched.size; });
		},
		fileLength: function (path) {
			var data = prefetched && prefetched.get(path);
			return data ? data.length : 0;
		},
		takeFileInto: function (path, view) {
			var data = prefetched && prefetched.get(path);
			if (data) view.set(data, 0);
			if (prefetched) prefetched.delete(path);
		},
		takeFile: function (path) {
			if (!prefetched) return new Uint8Array(0);
			var data = prefetched.get(path);
			prefetched.delete(path);
			return data || new Uint8Array(0);
		},
		assetManifest: function () {
			return fetch("web-assets.txt", { cache: "no-cache" }).then(function (r) {
				if (!r.ok) return "";
				return r.text();
			}).catch(function () { return ""; });
		}
	};
}
