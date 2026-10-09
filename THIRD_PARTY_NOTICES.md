# Third-Party Notices

이 프로그램은 아래 서드파티 구성 요소를 함께 배포합니다.

## VirtualDesktopAccessor

- 저장소: https://github.com/Ciantic/VirtualDesktopAccessor
- 파일: `vda/<릴리즈>/VirtualDesktopAccessor.dll`
- 다운로드: `https://github.com/Ciantic/VirtualDesktopAccessor/releases/download/<릴리즈>/VirtualDesktopAccessor.dll`
- 라이선스: MIT

Windows 빌드마다 전용 DLL이 필요하므로 아래 릴리즈를 모두 배포하고, 실행 시 Windows 빌드에 맞는 하나만 로드합니다.
지원 빌드는 같은 빌드 계열(22631→22621, 26200·26300→26100)에서 해당 UBR 이상이며, 더 높은 항목이 있으면 그 항목이 우선합니다.
기준 데이터는 `DesktopSessionManager.csproj`의 `VdaRelease` 항목입니다.

| 릴리즈 | 지원 Windows 빌드 | SHA256 |
|---|---|---|
| `2024-12-16-windows11` | 26100.2605+ (24H2, 25H2) | `8740c572a1c000e3b87ffeb1e4c397eae9af3bd4a2abdc3bcffacab4493f8ff5` |
| `2024-01-25-windows11` | 22621.3085+ (22H2, 23H2) | `f78ff6334f6c0ef5175ec0819026cec31d421a564b9ed1ee1ac4b6ed98d4f999` |
| `2023-11-10-windows11` | 22621.2215+ (22H2, 23H2) | `6fde6f5f409b026688f01ac44973a9d95fb37ae71632e4cbdd8bdd8c7f7c9c17` |
| `2023-02-22-windows11` | 22621.0+ (22H2) | `f6dc5f4acd7f1553769eda0a84e63fbc004e530132fe66fa69ae944fab8a234e` |

```
Copyright (c) 2015-2023 Jari Otto Oskari Pennanen

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
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
