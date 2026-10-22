# Third-party notices

## LibHac 0.19.0

Copyright © 2018-2022 Alex Barney

MIT License

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


## React / React DOM

MIT License

Copyright (c) Meta Platforms, Inc. and affiliates.

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


## Other dependencies

Microsoft.Data.Sqlite and transitive managed dependencies: Microsoft and SQLitePCLRaw contributors, MIT. SQLite native engine: public domain. Consult the license notices in the corresponding NuGet packages when redistributing binaries.

Vite and TypeScript are build tools; see frontend/package-lock.json for exact development dependencies.

## Optional Linux credential storage

The Secret Service adapter dynamically calls the system-installed libsecret and GLib/GIO libraries. These shared libraries and a Secret Service daemon are not bundled with RyuSync. See [libsecret's upstream license](https://github.com/GNOME/libsecret/blob/main/COPYING) and your distribution's package notices for their LGPL terms. RyuSync does not copy libsecret implementation code or shell out with passwords on command lines.

## Optional online game metadata

The user-initiated game metadata lookup uses the public [Nlib API](https://github.com/ghost-land/Nlib-API), which documents TitleDB as a data source. RyuSync uses an independently implemented HTTP client and does not incorporate Nlib server code. Game names and images belong to their respective rights holders. Images are downloaded into the user's local state database only on request and are not bundled with RyuSync source or releases. Service availability and language coverage may vary.
