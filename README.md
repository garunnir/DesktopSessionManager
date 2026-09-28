# DesktopSessionManager
LLM으로 만든 내 작업환경 저장/로드 프로그램

## 의존성

- .NET 8 (WinForms). 빌드에는 .NET 8 이상 SDK가 필요합니다.
- [VirtualDesktopAccessor](https://github.com/Ciantic/VirtualDesktopAccessor) 릴리즈 `2024-12-16-windows11`의 `VirtualDesktopAccessor.dll` (MIT)
  - Windows 11 24H2 26100.2605 이상이 필요합니다.
  - DLL은 저장소에 포함하지 않습니다. 빌드 시 공식 릴리즈에서 내려받고 SHA256을 확인합니다 (`DesktopSessionManager.csproj`).
  - 버전·해시·라이선스 전문은 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)에 있습니다.

## 빌드

`build.cmd`를 실행하면 `dist/`에 EXE, DLL, `plugins/`, `THIRD_PARTY_NOTICES.md`가 만들어집니다.

## 개발 중 실행

`dotnet run` 또는 VS Code에서 F5 (C# 확장 필요). Debug 빌드는 창 제목에 `[DEV]`가 붙고,
프로필을 `%LOCALAPPDATA%\DesktopSessionManager-Dev\Profiles`에 따로 저장합니다.
실행 중 앱에서 수정한 플러그인은 `bin/` 쪽 사본에 저장되므로, 유지하려면 `plugins/`로 옮기세요.

## Aseprite 열린 파일 복원

Aseprite는 창 제목에 현재 탭 하나만 보여 주므로, 열린 파일 목록은 동봉한 확장이 기록합니다.
`integrations/aseprite/desktop-session-bridge` 폴더를 Aseprite 사용자 설정 폴더의 `extensions/`에 복사하고
(포터블은 `aseprite.exe` 옆, 설치형은 `%APPDATA%\Aseprite`) Aseprite를 다시 시작하세요.
처음 파일을 열 때 스크립트의 파일 쓰기 권한을 묻는데, 전체 신뢰를 허용하면 됩니다.
확장이 없으면 [선택 창 프로젝트 지정]으로 열 파일을 직접 고를 수 있습니다. 저장되지 않은 변경은 복원하지 않습니다.
