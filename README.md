# DesktopSessionManager
LLM으로 만든 내 작업환경 저장/로드 프로그램

## 의존성

- .NET 8 (WinForms). 빌드에는 .NET 8 이상 SDK가 필요합니다.
- [VirtualDesktopAccessor](https://github.com/Ciantic/VirtualDesktopAccessor)의 `VirtualDesktopAccessor.dll` (MIT)
  - Windows 11 22H2(22621) 이상이 필요합니다. 21H2(22000)와 Windows 10은 지원하지 않습니다.
  - VDA는 Windows 빌드마다 전용 DLL이 필요합니다. 여러 릴리즈를 `vda/<릴리즈>/`에 함께 배포하고,
    실행할 때 Windows 빌드와 UBR을 확인해 맞는 DLL 하나만 SHA256 확인 후 로드합니다. 맞는 DLL이 없으면 아무것도 로드하지 않습니다.
  - 릴리즈와 지원 빌드 표는 `DesktopSessionManager.csproj`의 `VdaRelease` 항목입니다. 빌드 시 공식 릴리즈에서 내려받고 SHA256을 확인합니다.
  - 새 릴리즈는 `.github/workflows/update-vda.yml`이 매일 감지·검증해 PR을 열고 저장소 소유자를 담당자로 지정합니다 (로컬: `scripts/update-vda.ps1`).
    PR을 머지하면 패치 버전이 자동 릴리즈되고, 앱은 다음 실행 때 그 버전으로 업데이트합니다. 외부 DLL이므로 머지는 사람이 확인합니다.
  - 릴리즈별 해시와 라이선스 전문은 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)에 있습니다.

## 빌드

`build.cmd`를 실행하면 `dist/`에 EXE, `vda/`, `plugins/`, `integrations/`, `THIRD_PARTY_NOTICES.md`가 만들어집니다
(`scripts/package.ps1`). 로컬 빌드는 버전이 0.0.0이며 업데이트 확인을 하지 않습니다.

## 릴리즈

- `v1.2.3` 형식의 태그를 push하면 `.github/workflows/release.yml`이 VDA 테이블 검증 → 패키징 → GitHub Release(zip, 지원 Windows 표, SHA256)를 만듭니다.
- VirtualDesktopAccessor 업데이트 PR을 머지하면 패치 버전을 올려 자동으로 릴리즈합니다.
- 앱은 시작할 때 GitHub의 최신 릴리즈를 확인하고, 새 버전이 있으면 업데이트할지 묻습니다 (**도움말 → 업데이트 확인**으로 직접 확인할 수도 있습니다).
  - 수락하면 zip을 내려받아 SHA256(릴리즈 에셋 digest, 없으면 릴리즈 노트)을 확인한 뒤 EXE 폴더의 파일을 교체하고 다시 시작합니다.
    실행 중인 EXE·DLL은 `*.update-old`로 이름을 바꿔 두고 다음 실행 때 지웁니다. 복사 중 실패하면 원래 파일로 되돌립니다.
  - `plugins/`는 없는 파일만 추가합니다. 수정한 설정이나 `.disabled`로 꺼 둔 플러그인은 그대로 둡니다.
  - 거절한 버전은 다음 시작 때 다시 묻지 않습니다 (상단 링크나 메뉴로 설치 가능). 실행 중인 Windows용 DLL이 없으면 매번 묻습니다.
  - EXE 폴더에 쓸 수 없으면(예: `C:\Program Files`) 다운로드 페이지를 엽니다.

## 개발 중 실행

`dotnet run` 또는 VS Code에서 F5 (C# 확장 필요). Debug 빌드는 창 제목에 `[DEV]`가 붙고,
프로필을 `%LOCALAPPDATA%\DesktopSessionManager-Dev\Profiles`에 따로 저장합니다.
실행 중 앱에서 수정한 플러그인은 `bin/` 쪽 사본에 저장되므로, 유지하려면 `plugins/`로 옮기세요.

## HUD·도구 창 포함

HUD·오버레이 같은 창은 기본적으로 목록에서 제외됩니다. **보기** 메뉴에서 창 종류별로 포함 여부를 켜고 끌 수 있고,
설정은 다음 실행에도 유지됩니다.

| 필터 | 포함하는 창 |
|---|---|
| 도구 창 | 작업 표시줄에 나오지 않는 도구 창 |
| 레이어드(투명) 창 | 투명 효과를 쓰는 창 |
| 숨겨진 창에 딸린 창 | 보이지 않는 소유자 창에 딸린 창 (보이는 창에 딸린 대화상자는 계속 제외) |
| 가상 데스크톱 밖의 창 | 어느 데스크톱에도 속하지 않는 창. "모든 데스크톱"으로 표시되고, 복원 시 데스크톱 이동 없이 실행·위치만 복원 |
| 데스크톱 밖의 Windows 시스템 창 | 위 창 중 셸, 가려진 창, Windows 폴더의 프로그램 창 |

예를 들어 AgentHud는 레이어드 창, 숨겨진 창에 딸린 창, 가상 데스크톱 밖의 창을 켜면 나타납니다.

## Aseprite 열린 파일 복원

Aseprite는 창 제목에 현재 탭 하나만 보여 주므로, 열린 파일 목록은 동봉한 확장이 기록합니다.
`integrations/aseprite/desktop-session-bridge` 폴더를 Aseprite 사용자 설정 폴더의 `extensions/`에 복사하고
(포터블은 `aseprite.exe` 옆, 설치형은 `%APPDATA%\Aseprite`) Aseprite를 다시 시작하세요.
처음 파일을 열 때 스크립트의 파일 쓰기 권한을 묻는데, 전체 신뢰를 허용하면 됩니다.
확장이 없으면 [선택 창 프로젝트 지정]으로 열 파일을 직접 고를 수 있습니다. 저장되지 않은 변경은 복원하지 않습니다.
