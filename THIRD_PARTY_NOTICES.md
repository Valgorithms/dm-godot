# Third-party notices

## OpenDream

The runtime links four wire-format definition files from OpenDream:
`DMCompiler/Bytecode/DreamProcOpcode.cs`, `OpcodeMetadata.cs`,
`OpcodeVerifier.cs`, and `StringFormatEncoder.cs`.

Source: https://github.com/OpenDreamProject/OpenDream

Pinned revision: `846775a06b8fa4639772cb3e759adcc0133d9711`.

OpenDream's compiler is built as an external tool from that revision. Its
RobustToolbox math dependency is fetched at the submodule revision
`af2a7d0406b410c4d960f0f4b34513651c6dc742`; retain that checkout's license notices
when redistributing compiler binaries. Neither upstream repository is copied
into this project's source archive. Bootstrap fetches them with their licenses.

OpenDream license:

```text
MIT License

Copyright (c) 2020 wixoa

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Compatibility targets and Godot

Civ13 and /tg/station source/assets are separate compatibility targets under
their own licenses. They are not relicensed or bundled here. Godot is an external
MIT-licensed engine: https://godotengine.org/license/ . The smoke fixture and its
procedurally generated DMI are original project assets under this project's MIT
license. No BYOND engine code or proprietary binaries are included.
