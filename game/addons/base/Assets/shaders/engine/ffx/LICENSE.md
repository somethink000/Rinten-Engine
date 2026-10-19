# AMD FidelityFX - FSR 3.1 upscaler and frame generation shaders

The files under `gpu/` are AMD's, from the FidelityFX SDK
(https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK, `Kits/FidelityFX`),
which lists every one of them under the MIT license below. Changed only where
their `#include` paths pointed into the SDK's own tree.

`gpu/fsr3upscaler/ffx_fsr3upscaler_callbacks_rinten.h` is the engine's, written
in place of AMD's `ffx_fsr3upscaler_callbacks_hlsl.h`; `../fsr3.slang` is the
engine's in place of AMD's eight `*_pass.hlsl` entry files.

Frame generation's `gpu/opticalflow/` and `gpu/frameinterpolation/` come from
`Kits/FidelityFX/framegeneration/fsr3` at commit 60f4ea8, changed only in their
`#include` paths. `ffx_opticalflow_callbacks_rinten.h` and
`ffx_frameinterpolation_callbacks_rinten.h` are the engine's, in place of AMD's
`*_callbacks_hlsl.h`; `../framegen.slang` is the engine's in place of AMD's
`*_pass.hlsl` entry files.

---

Copyright (C) Advanced Micro Devices, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
