# M-Dir — Q-Dir 기능 복제 개발 계획

> 분석 대상: `D:\Q-Dir` (Q-Dir 12.77, Nenad Hrg / SoftwareOK.com, 2026-08-25 빌드)
> 작성일: 2026-10-28

---

## 1. 분석 결과

### 1.1 폴더 구성

| 파일 | 크기 | 역할 |
|---|---|---|
| `Q-Dir.exe` | 1.39 MB | 단일 실행 파일 (설치 프로그램·제거 프로그램·언어팩 모두 내장) |
| `Q-Dir.ini` | 10 KB | 전역 설정 (포터블 모드, 언어, 색상 필터, 열 상태) |
| `start.qdr` | 20 KB | 마지막 작업 공간(Workspace) 자동 저장본 (UTF-16 LE) |
| `Favoriten\Quick-Link\` | (빈 폴더) | 즐겨찾기 / 퀵링크 저장소 (`.lnk` 파일 기반) |

### 1.2 실행 파일 기술 구조

- **x86 32비트 Win32 GUI**, Visual C++ 6.0 링커, 순수 Win32 API + **ATL/WTL** (`WTL_XSplitterWindow`, `AXWIN Frame Window` 문자열 확인)
- 코드 서명: GlobalSign EV 인증서(유효)
- **파일 목록은 직접 그리지 않음** — 메인 패널은 `IShellBrowser`를 직접 구현해 Windows 셸의 `IShellView`(`SHELLDLL_DefView`)를 호스팅함(자세한 내용은 1.6 REA 분석 참고). 그래서 우클릭 메뉴, 썸네일, 열, 그룹화, 드래그앤드롭이 Windows 탐색기와 같음
- 주요 import:
  - `SHELL32`: `SHGetDesktopFolder`, `SHFileOperationW`, `SHGetFileInfoW`, `ShellExecuteExW`, `DragAcceptFiles`, `Shell_NotifyIconW`(트레이), `SHAppBarMessage`
  - `ole32`: `RegisterDragDrop`/`DoDragDrop`, `OleSetClipboard`/`OleGetClipboard`, `CoCreateInstance`
  - `gdiplus`: 이미지 미리보기·썸네일·회전
  - `comdlg32`/`WINSPOOL`: 인쇄, 페이지 설정, 색상 선택
  - `ADVAPI32`: 레지스트리(셸 메뉴 등록, 자동 시작, 제거 정보), `AdjustTokenPrivileges`(SeBackup/SeRestore 권한), `CryptHashData`
  - `COMCTL32`: ImageList, StatusBar, PropertySheet(설정 대화상자)
  - 동적 로드: `Magnification.dll`(돋보기), `Propsys.dll`, `UxTheme.dll`·`dwmapi.dll`(다크 모드), `Wtsapi32.dll`
- 내장 COM 컨트롤: `{8856F961-…}` WebBrowser(HTML/문서 미리보기), `{6BF52A52-…}` Windows Media Player(미디어 미리보기)
- 리소스: 메뉴 10개, 대화상자 57개, 단축키 테이블 5개, 아이콘 31개, PNG/BMP 다수
- **언어팩 40개**(`IMG/EN`, `IMG/KR`, `IMG/DE` …)가 자체 압축 형식으로 내장됨. 메뉴 텍스트는 숫자 ID(예: `10001`)이고 실행 시 언어팩 문자열로 바뀜

### 1.3 설정 파일 형식

**Q-Dir.ini** (INI, `WritePrivateProfileStringW`):
- `[Start]` 언어 ID, 창 위치(`WinRC`), 디자인 모드
- `[Program] portable_install=1`: 레지스트리 대신 exe 옆 INI 사용
- `[Filter2]` 색상 필터: `패턴=사용여부=RRGGBB=…`
  - 특수 키워드: `#Hidden`(숨김 파일), `#DIR`(폴더), `#BG`/`#BG-A`(배경)
  - 예: `*.zip;*.rar;*.7z` → `00AA00`(초록), `*.exe;*.dll;*.bat` → `FF0000`(빨강)
- `[Column_OS_10.0_Ploder1..4]` 패널별 열/정렬 상태: 셸 `IPropertyBag` 스트림을 `%XX`로 인코딩한 값

**start.qdr** (Workspace, UTF-16 INI 형식):
```
ViewMode=0                       ← 레이아웃 (1/2/3/4 패널 배치)
Folder1=<PIDL %XX 인코딩>&ViewModeFlags1=…&ViewMode1=4
SP0/SP1/SP2/SPT=5000              ← 분할선 위치 (1/10000 비율)
tree_mode / Tree1..4 / TreeNsplit ← 패널별 트리 표시·분할 위치
Vorschau / VorschauPos            ← 미리보기 창 표시·위치
Focus / colorthema
ColsAndSortN / ColsIDN            ← 패널별 열·정렬
windowplacement=x;y;w;h|sw=1
```
폴더 경로는 **PIDL**(ITEMIDLIST) 이진값으로 저장함. 그래서 "내 PC", 라이브러리 같은 가상 폴더도 저장됨.

### 1.4 기능 목록 (리소스·문자열·설정 키에서 확인)

| 영역 | 기능 |
|---|---|
| **레이아웃** | 1~4개 패널, 레이아웃 13종(가로/세로/3분할 변형/4분할), 분할선 드래그, 비율 저장 |
| **패널** | 패널마다 탭, 주소창(자동 완성·드롭다운·드라이브 목록), 툴바, 상태 표시줄, 필터 |
| **파일 목록** | 셸 기본 뷰: 큰/작은 아이콘, 목록, 자세히, 타일, 내용, 썸네일 크기(96/128/160/192/256). 열 사용자화, 열 너비 자동 맞춤, 그룹화, 행 전체 선택, 체크박스 선택 |
| **트리** | 패널별 트리 또는 공용 트리(`tree_mode`), 자동 펼침, 단일 펼침, 목록과 동기화, 색상 |
| **탭** | 새 탭/닫기(가운데 클릭·더블클릭), 여러 줄 탭, 탭 그룹, 탭 너비 모드, 툴팁, 기본 탭 |
| **탐색** | 뒤로/앞으로/위로, 패널 간 포커스 전환(Ctrl+Alt+1~4, F6), Ctrl+Tab 탭 전환 |
| **파일 작업** | 복사/잘라내기/붙여넣기/삭제/이름 바꾸기(셸 위임), 새 폴더, 하드 링크 생성, 이동 확인 |
| **즐겨찾기** | 즐겨찾기 폴더(.lnk), 퀵링크, 관리 대화상자(트리 드래그 정렬, 아이콘 변경), 시작 시 펼침 |
| **색상 필터** | 확장자·숨김·폴더별 글자색/배경색, 가져오기/내보내기/추천 |
| **파일 필터** | 패널별 포함 필터 + 제외(Negative) 필터(예: `Thumbs.db;*.bak`), 폴더 필터, 최근 필터 |
| **미리보기** | 이미지(GDI+), 미디어(WMP), HTML/문서(WebBrowser), 텍스트. 확대 및 회전 |
| **작업 공간** | `.qdr` 저장/불러오기(Ctrl+S / Ctrl+O), 시작 시 자동 복원, 새 작업 공간 |
| **폴더 크기** | 자세히 보기에 폴더 크기 열 계산(`fld_size`, 정렬 옵션) |
| **도구** | 다중 이름 바꾸기, 폴더 비교(빠른 비교/전문가 모드), Folder-to-Folder 복사, SHA256 해시, 이미지 중복 찾기(FSI 연동), 검색(MS-Search/Q-Search 베타) |
| **목록 내보내기·인쇄** | TXT(UTF-8/16), CSV, HTML, XLS 내보내기. 인쇄 미리보기, 출력 비율, 페이지 설정 |
| **경로 복사** | 경로·파일명·전체 경로 복사, 여기서 cmd/PowerShell/Explorer 열기 |
| **시스템 통합** | 폴더 우클릭 메뉴 "*Q-Dir"(`\shell\*Q-Dir\command`), 자동 시작(`Run` 키), 트레이 최소화(Ctrl/Shift+Esc), 전체 화면(F11) |
| **설치** | 포터블 ↔ 설치형, Quick Installer / Uninstaller 내장, 관리자 권한 실행(`-runas`) |
| **UI 사용자화** | 클래식/플랫 디자인, 다크 모드, 색상 테마, UI 확대(60~200%), 툴바 편집, 단축키 편집, 돋보기(Magnifier) |
| **다국어** | 40개 언어, 번역 편집기 내장 |
| **명령줄** | `.qdr` 파일 열기, 폴더 경로 인자, `-explore`, `-install`, `-uninstall`, `/Browse` |

### 1.5 확인된 기본 단축키

| 키 | 기능 | 키 | 기능 |
|---|---|---|---|
| F5 | 새로 고침 | Ctrl+N / O / S | 새 작업 공간 / 열기 / 저장 |
| F4 | 주소창 포커스 | Ctrl+P | 인쇄 |
| F6 / Shift+F6 | 다음/이전 패널 | Ctrl+Tab, Ctrl+←/→ | 탭 전환 |
| Ctrl+Alt+1~4 | 패널 1~4로 이동 | Alt+Back | 뒤로 |
| F11 / Alt+F11 | 전체 화면 / 메뉴 숨김 | F12 | 미리보기 토글 |
| Ctrl+Alt+A | 새 탭 | Alt+L | 레이아웃 선택 |
| Ctrl+R | 다중 이름 바꾸기 | Ctrl+Shift+V | 붙여넣기 관련 |

### 1.6 REA 분석 (디컴파일 근거)

[REA](https://github.com/morluto/rea) 6.8.0 + Ghidra 12.1.4로 `Q-Dir.exe`를 분석했습니다(함수 4,470개, 문자열 1,939개). 주소는 Ghidra 기준 가상 주소입니다.

**패널 호스팅 방식**

| 근거 | 주소 | 결론 |
|---|---|---|
| QueryInterface가 `IUnknown`·`IOleWindow`·`IShellBrowser`·`ICommDlgBrowser`·`ICommDlgBrowser2`·`IPersistFolder2`·`IServiceProvider`에 응답 | `0x4510d8`, `0x48d214` | 메인 패널은 **`IShellBrowser`를 직접 구현**하고 `IShellView::CreateViewWindow`로 DefView를 띄움(`IShellView2/3` GUID 없음) |
| 패널 뷰에 `LVM_GETSELECTEDCOUNT`(0x1032) 전송, `FindWindowEx(DefView, "SysListView32")` | `0x459914`, `0x451d60` | 탐색기가 아닌 브라우저에 호스팅된 DefView는 **기존 `SysListView32`**를 씀 |
| `CoCreateInstance(CLSID_ExplorerBrowser)`가 대화상자 프로시저의 `WM_INITDIALOG`에서만 호출 | `0x401a98`, `0x4018b6` | `IExplorerBrowser`는 열 편집 대화상자 같은 **보조 창에만** 씀. `IColumnManager`(`0x402099`)로 열을 다루고, 탐색 창 분할선은 마우스 메시지를 흉내 내 조정(`0x40287e`) |

**색상 필터**

- 기본 프리셋 15종을 7필드 표(패턴, 사용, 글자색, 배경색, 굵게…)로 만들어 INI `[Filter2]`에 기록함(`0x46c2cd`)
- 규칙 컴파일: 패턴을 `;`로 나누고 대문자로 바꿈. `#BG`/`#BG-A`(활성 패널 배경)는 전역값으로 따로 저장함(`0x46c03f`)
- 매칭: `IShellFolder::GetDisplayNameOf`로 이름을, `GetAttributesOf`로 속성을 읽어 와일드카드로 비교함. `#DIR` = `SFGAO_FOLDER`, `#Hidden` = `SFGAO_HIDDEN|SFGAO_GHOSTED`(`0x46d863`)
- 그리기: DefView를 서브클래스해 목록의 `NM_CUSTOMDRAW`를 처리함. `CDDS_ITEMPREPAINT`에서 `dwItemSpec`으로 PIDL을 구해 `clrText`/`clrTextBk`를 설정하고 `CDRF_NEWFONT`를 반환함(`0x452b20`)
- 목록 서브클래스: `LVM_SETBKCOLOR`·`LVM_SETTEXTBKCOLOR`를 가로채 배경색 강제, Shift+휠 → 가로 스크롤, `LVM_SETSELECTEDCOLUMN(-1)`로 정렬 열 강조 끄기(`0x451b2a`)
- 트리 항목에도 같은 규칙 적용(`TVM_GETITEMRECT` + `INameSpaceTreeControl::HitTest`, `0x461163`)

**파일 필터** — `IFolderFilter`는 쓰지 않고 **`ICommDlgBrowser::IncludeObject`**로 구현함(`0x456a3f`)
- 이름을 소문자로 바꿔 패턴 목록과 와일드카드 비교(`0x456b7b`)
- `by_auto_wildcard`: 패턴이 하나면 `*패턴*`으로 감쌈 / `by_negativ`: 결과 반전 / `by_dir_filter`가 꺼져 있으면 폴더는 항상 표시(`0x456b2b`)

**그 밖의 구현**

| 기능 | 구현 | 주소 |
|---|---|---|
| 열·정렬 저장 | DefView 상태 바이트를 `%XX`로 인코딩 + `\|CODEMODE1\|길이\|체크섬`. 폴더 종류(CLSID)별 INI `Spatlen_` 키에 저장(`IShellBrowser::GetViewStateStream`) | `0x47771e`, `0x479dff` |
| 트리 | `CLSID_NamespaceTreeControl` + `INameSpaceTreeControl::Initialize`. 플래그 HASEXPANDOS·HASLINES·HORIZONTALSCROLL·SHOWSELECTIONALWAYS·TABSTOP(+ALLOWJUNCTIONS/NOEDITLABELS/SPRINGEXPAND 옵션). 안쪽 `SysTreeView32` 서브클래스 | `0x460bb6` |
| 파일 작업 | `IFileOperation` + `FOF_ALLOWUNDO`, Shift를 누르면 영구 삭제 | `0x45f296` |
| 우클릭 메뉴 | `IContextMenu`/`IContextMenu2`/`IContextMenu3` 직접 처리 | `0x48d72c`, `0x48f18c` |
| 주소창 | `IAutoComplete2` | `0x4666c7` |
| 미리보기 | `IPreviewHandler` 없음 → WebBrowser/WMP 컨트롤 + GDI+ | — |

**REA 정적 검사**: import 14개 DLL·542개(imphash `a039e968…`), TLS 콜백 없음, 로드 설정 없음(VC6 빌드라 CFG/GS 없음).

---

## 2. 기술 선택

### 권장안: C# (.NET 10) + WinForms + 셸 COM 인터롭

- 이 PC에는 **.NET SDK 10.0.400과 Git이 설치되어 있고, MSVC·CMake는 없습니다.** 추가 설치 없이 바로 시작할 수 있습니다.
- 핵심은 Q-Dir처럼 **`IExplorerBrowser`를 패널마다 호스팅**하는 것입니다. 파일 목록·우클릭 메뉴·썸네일·드래그앤드롭·열 관리를 Windows가 처리하므로 직접 구현할 양이 크게 줄어듭니다.
- WinForms는 Win32 HWND 기반이라 `IExplorerBrowser.Initialize(hwnd, …)`로 붙이기 쉽습니다(WPF보다 유리).
- COM 인터페이스는 `[GeneratedComInterface]`(.NET 8+ 소스 생성 COM)나 직접 작성한 `[ComImport]` 정의를 씁니다. `IExplorerBrowser`, `IExplorerBrowserEvents`, `IShellItem`, `IFolderView2`, `IShellView`, `IContextMenu`, `IFileOperation` 등이 필요합니다.
- 배포: `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` → 단일 exe(포터블)

### 대안: C++20 + Win32/WTL

원본과 같은 구조라 실행 파일이 작고 빠르지만, Visual Studio Build Tools를 설치해야 하고 개발 속도가 느립니다. 성능·용량이 최우선일 때만 선택하세요.

---

## 3. 단계별 개발 계획

각 단계는 끝날 때 **실행 가능한 상태**가 되도록 나눴습니다. 기간은 1인 개발 기준 대략치입니다.

### 0단계 — 프로젝트 골격 (1~2일)
- `git init`, `dotnet new winforms -n MDir`, 솔루션 구조:
  ```
  src/MDir/            WinForms 앱
    Shell/             COM 인터롭 (IExplorerBrowser, IShellItem, PIDL 유틸)
    Panes/             PaneControl, TabStrip, AddressBar
    Layout/            레이아웃 엔진, 스플리터
    Settings/          INI/Workspace 직렬화
    Features/          필터, 색상, 즐겨찾기, 미리보기, 도구
  tests/MDir.Tests/    xUnit (설정 직렬화, 필터 매칭, 레이아웃 계산)
  ```
- STA 스레드, `Application.SetHighDpiMode(PerMonitorV2)`, 앱 매니페스트(Common Controls v6, DPI aware)
- **완료 기준**: 빈 메인 창이 뜨고 빌드·테스트 파이프라인이 동작함

### 1단계 — 셸 브라우저 패널 1개 (3~5일) ★ 핵심 위험 요소
- `ExplorerBrowserHost : Control`: `CoCreateInstance(CLSID_ExplorerBrowser)` → `Initialize` → `BrowseToIDList`
- 크기 변경 시 `SetRect`. `IExplorerBrowserEvents`로 탐색 완료 이벤트를 받고, `IFolderView2`로 뷰 모드와 선택 항목을 가져옴
- `IServiceProvider`/`ICommDlgBrowser` 구현(포커스·더블클릭 처리), 키보드 메시지 전달(`TranslateAccelerator`)
- 뒤로/앞으로/위로(`BrowseToIDList` + `SBSP_NAVIGATEBACK` 등)
- **완료 기준**: 패널 하나에서 탐색기처럼 폴더 탐색, 우클릭 메뉴, 드래그앤드롭, 뷰 모드 전환이 됨

### 2단계 — 다중 패널 & 레이아웃 엔진 (3~4일)
- `PaneControl` = 주소창 + 툴바 + ExplorerBrowserHost + 상태 표시줄
- 레이아웃 엔진: 13종 레이아웃을 "분할 트리"(가로/세로 노드 + 비율)로 정의. 기본: 1, 2(좌우), 2(상하), 3×3종, 4분할
- 분할선은 비율(0~10000)로 저장(원본의 `SP0/SP1/SP2/SPT`와 같은 방식)
- 활성 패널 표시(테두리 강조), 패널 간 포커스 전환(F6, Ctrl+Alt+1~4)
- **완료 기준**: 레이아웃 메뉴로 1~4 패널을 바꿀 수 있고 분할선을 드래그할 수 있음

### 3단계 — 주소창 & 탭 (4~5일)
- 주소창: 경로 직접 입력(`SHParseDisplayName` → PIDL), `shell:` 경로 지원, 자동 완성(`IAutoComplete2` + `ACListISF`), 드라이브/특수 폴더 드롭다운, 선택 사항으로 브레드크럼
- 탭: 패널마다 TabStrip(직접 그리기). 새 탭/닫기/복제, 가운데 클릭 닫기, 드래그로 순서 변경, 여러 줄 모드, 툴팁
- 탭마다 탐색 기록 보관. 브라우저 인스턴스는 활성 탭만 유지하고 나머지는 PIDL과 뷰 상태만 저장(메모리 절약)
- **완료 기준**: 각 패널에서 여러 탭을 열고 주소를 직접 입력해 이동할 수 있음

### 4단계 — 설정 & 작업 공간 저장 (3일)
- `IniFile` 클래스(UTF-16, `GetPrivateProfileString` P/Invoke 또는 직접 파서)
- 포터블 모드: exe 옆에 `M-Dir.ini`가 있으면 사용하고, 없으면 `%APPDATA%\M-Dir`
- Workspace(`.mdr`) 형식: 레이아웃, 분할 비율, 패널·탭별 PIDL(`%XX` 인코딩), 뷰 모드, 트리 상태, 미리보기 상태, 창 위치
- 패널별 열·정렬 저장: `IFolderView2.GetSortColumns`/`IColumnManager`로 열 상태를 읽고 다시 적용
- 시작 시 `start.mdr` 자동 복원, 종료 시 저장. Ctrl+N/O/S
- (선택) **Q-Dir `.qdr` 가져오기**: 위 1.3의 형식대로 파싱해 PIDL과 레이아웃 변환
- **완료 기준**: 다시 시작하면 패널·탭·분할·창 위치가 그대로 복원됨

### 5단계 — 폴더 트리 (3~4일)
- `INameSpaceTreeControl`(Windows 탐색기 트리)을 호스팅. 패널별 트리 모드와 공용 트리 모드 지원
- 목록과 트리 동기화(목록에서 이동하면 트리 선택 이동, 트리를 클릭하면 패널 이동)
- 옵션: 자동 펼침, 단일 펼침, 행 전체 선택, 선 숨김, 들여쓰기
- **완료 기준**: 트리를 켜고 끌 수 있고, 목록과 양방향으로 동기화됨

### 6단계 — 색상 필터 & 파일 필터 (3~4일)
- **ListView 모드 전환**: `IExplorerBrowser.Initialize` 전에 `IFolderViewOptions.SetFolderViewOptions(FVO_VISTALAYOUT)`를 지정 → 목록이 DirectUI 대신 `SysListView32`가 됨 (✅ 2026-10-28 실측: 기본값은 `DirectUIHWND`, 지정 시 `SysListView32`)
- **파일 필터**: Q-Dir과 같이 `ICommDlgBrowser::IncludeObject`로 구현. `IUnknown_SetSite`로 `IServiceProvider`를 넘기고 `QueryService(SID_SExplorerBrowserFrame)`에서 `ICommDlgBrowser`를 반환. 옵션은 원본과 같게: 자동 와일드카드(`*패턴*`), 제외 필터(반전), 폴더 제외 여부. 필터 입력 UI와 최근 필터 기록
  - `ICommDlgBrowser::OnStateChange(CDBOSC_SELCHANGE)`로 선택 변경 이벤트를 받아 1단계의 250ms 폴링을 대체
- **색상 필터**: DefView(`SHELLDLL_DefView`)를 서브클래스해 목록의 `NM_CUSTOMDRAW`를 처리. `CDDS_ITEMPREPAINT`에서 `IFolderView.Item(dwItemSpec)`으로 PIDL을 얻어 규칙과 매칭하고 `clrText`/`clrTextBk`를 설정
  - 규칙 형식은 원본과 호환: `패턴;패턴=사용=RRGGBB=…`, 특수 키 `#Hidden`(SFGAO_HIDDEN|GHOSTED), `#DIR`(SFGAO_FOLDER), `#BG`/`#BG-A`(배경/활성 패널 배경)
  - 목록 서브클래스로 배경색 강제, Shift+휠 가로 스크롤, 정렬 열 강조 끄기(옵션)
- 색상 규칙 편집 대화상자(가져오기/내보내기)
- **완료 기준**: `*.exe`가 빨간색으로 보이고, `*.bak`을 제외 필터로 숨길 수 있음
- 참고: ListView 모드에서는 Windows 11 탐색기의 일부 새 UI(예: 그룹 헤더 모양)가 기존 스타일로 보임. Q-Dir도 같은 모양임

### 7단계 — 즐겨찾기 / 퀵링크 (2~3일)
- 즐겨찾기 폴더(`Favorites\`)에 `.lnk`를 저장(`IShellLink`). 하위 폴더는 메뉴 계층이 됨
- 메뉴·툴바 드롭다운으로 표시하고, Ctrl+클릭하면 새 탭에서 엶
- 관리 대화상자: 트리 뷰, 드래그 정렬, 이름 바꾸기, 아이콘 변경
- **완료 기준**: 현재 폴더를 즐겨찾기에 추가하고 메뉴에서 이동할 수 있음

### 8단계 — 미리보기 창 (3~4일)
- 1순위: 셸 미리보기 처리기(`IPreviewHandler`)를 호스팅 — Office, PDF, 텍스트, 이미지를 OS가 처리
- 대체 렌더러: 이미지(확대·회전), 텍스트(인코딩 감지), 미디어(`Windows.Media` 또는 WebView2 `<video>`)
- 선택이 바뀔 때 디바운스(150ms)와 비동기 로드
- **완료 기준**: F12로 미리보기를 켜고, 선택한 파일의 내용이 표시됨

### 9단계 — 메뉴, 툴바, 단축키, 파일 작업 (3~4일)
- 메인 메뉴(파일/편집/보기/즐겨찾기/도구/창/도움말)를 1.4 기능 목록에 맞춰 구성
- 명령 시스템: `Command` 레지스트리(ID → 실행 함수 + 활성화 조건) — 메뉴·툴바·단축키가 같은 명령을 공유
- 단축키 편집 가능(INI 저장), 기본값은 1.5 표
- 파일 작업은 `IFileOperation`(진행률·실행 취소를 셸이 처리), 다른 패널로 복사/이동("반대편 패널로")
- 경로 복사 3종, 여기서 cmd/PowerShell/탐색기 열기
- **완료 기준**: 모든 명령이 메뉴·툴바·단축키로 동작함

### 10단계 — 부가 도구 (5~7일, 우선순위별)
1. 폴더 크기 계산(백그라운드 스레드, 캐시, 상태 표시줄·열 표시)
2. 목록 내보내기(TXT/CSV/HTML/XLSX) 및 인쇄(미리보기 포함)
3. 다중 이름 바꾸기(패턴·번호·찾기/바꾸기, 미리보기 목록)
4. 폴더 비교(이름·크기·날짜, 빠른 비교 / 해시 비교)
5. Folder-to-Folder 복사/동기화
6. 파일 해시(SHA-256), 하드 링크 생성
7. 검색(Windows Search `search-ms:` 연동 + 간단한 자체 검색)

### 11단계 — 시스템 통합 & 사용자화 (3~4일)
- 트레이 최소화(`NotifyIcon`), 자동 시작, 전체 화면, 단일 인스턴스 옵션(Mutex + 명명된 파이프로 인자 전달)
- 탐색기 폴더 우클릭 메뉴 등록/해제(`HKCU\Software\Classes\Directory\shell\M-Dir`)
- 명령줄: 폴더 경로, `.mdr` 파일, `-explore`
- 다크 모드(`DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`, `SetWindowTheme(hwnd,"DarkMode_Explorer")`), UI 확대 배율
- 툴바 편집 대화상자

### 12단계 — 다국어 & 배포 (2~3일)
- 문자열을 `.resx`(한국어/영어 우선)로 관리하고 런타임에 언어 전환
- 단일 exe로 self-contained publish, 포터블 zip, (선택) 설치 프로그램
- 회귀 테스트 체크리스트(아래 4장)로 검증

**총 예상: 약 8~11주** (핵심 MVP인 0~4단계: 약 3주)

### 진행 상황

| 단계 | 상태 | 비고 |
|---|---|---|
| M-Finder 연동 | ✅ 완료 (2026-11-14) | 도구 모음의 레이아웃 드롭다운 뒤 M-Finder 아이콘(`view.finder`, Ctrl+Shift+M)으로 오른쪽 검색 창을 엽니다. 배치는 패널 \| M-Finder \| 미리보기(좌우 나란히, 폭 비율은 `.mdr`의 `Finder`·`FinderSplit`). 검색은 M-Finder(Python)가 하고 M-Dir는 사용자별 명명된 파이프(`M-Finder-<SID>`)로 요청합니다(`Finder/FinderClient.cs`). 실행 중인 M-Finder가 없으면 `gui --headless`로 창 없이 띄우고, 직접 띄운 것만 M-Dir 종료 시 `quit`으로 끝냅니다. M-Finder 위치는 `M-Dir.ini [Finder] Path`(exe 또는 소스의 `mfinder\__main__.py`, 도구 > M-Finder 위치 지정). 가상 목록(200개씩 페이지 요청), 필터·대소문자·단어·경로·정규식, 열 클릭 정렬, 색인 진행 표시, 색인 변경 시 2초 간격 새로 고침. 결과를 클릭하면 미리보기 창이 그 파일을 보여 주고(◀▶·슬라이드쇼도 결과 목록을 따름), 패널에서 다시 선택하면 패널 쪽으로 돌아갑니다. Enter = 열기(폴더는 활성 패널에서), Ctrl+Enter = 폴더에서 보기, 끌어서 놓기, 경로 복사. M-Finder 쪽: `--headless`, IPC `filters`·`quit`, `status`의 `ready`·`rev`, 같은 검색의 결과 목록 재사용. 포터블 사본 + 실제 M-Finder로 연결·자동 시작·자동 종료를 확인함. 테스트 524개 중 522개 통과(실패 2개는 임시 폴더의 정션 삭제 권한 문제로 이번 변경과 무관). |
| 0단계 | ✅ 완료 (2026-10-28) | `MDir.slnx`, `src/MDir`(WinForms, .NET 10, 실행 파일 `M-Dir.exe`), `tests/MDir.Tests`(xUnit) |
| 1단계 | ✅ 완료 (2026-10-28) | `ExplorerBrowserHost`(IExplorerBrowser 호스팅), 자체 탐색 기록, 패널 단위 보기 모드, 키보드 라우팅(화살표·Tab·Backspace·셸 단축키), 마우스 뒤로/앞으로 버튼, 주소창 자동 완성. 테스트 14개 통과(통합 테스트 3개는 실제 셸 뷰로 검증) |
| 12단계 | ✅ 대부분 완료 (2026-10-30), 설치 프로그램 제외 | 한국어/영어 전환(`Loc`, 내장 영어 표 + `Lang\*.lng` 언어 팩), 자체 포함 단일 exe 게시(`publish.ps1`, 약 49MB)와 포터블 zip. 번역하지 못한 동적 문구가 남아 있음. |
| 11단계 | ✅ 완료 (2026-10-30), 실제 탐색기 메뉴·트레이 표시 미확인 | 명령줄(`-explore`·`-new`·`-minimized`), 단일 인스턴스(뮤텍스+명명된 파이프, 실제 exe로 확인), 트레이 최소화, Windows 시작 시 실행, 탐색기 폴더 우클릭 메뉴 등록(HKCU, 포터블에서는 꺼짐), 다크 모드, UI 배율, 도구 모음 편집. |
| 10단계 | ✅ 대부분 완료 (2026-10-29), 검색·인쇄 제외 | 폴더 크기(`FolderSizeCalculator`·캐시·`SelectionSizer`, 상태 표시줄 + 자동 계산 옵션), 목록 내보내기 TXT/CSV/HTML/XLSX(`ListExporter`), 여러 파일 이름 바꾸기(`RenamePlanner`/`RenameExecutor`: 마스크·번호·찾기/바꾸기·정규식·대소문자, 미리보기, 되돌리기), 폴더 비교(빠른/내용 비교)와 복사·양방향 동기화(`FolderComparer`·`SyncPlanner`, 삭제하지 않음), 파일 해시 SHA-256/SHA-1/MD5(`FileHasher`, 붙여넣은 체크섬과 대조)와 하드 링크(`HardLinks`). 메뉴 항목 7개 추가(`file.exportList`, `view.autoFolderSize`, `tools.folderSize`·`multiRename`(Ctrl+R)·`compareFolders`·`hash`·`createHardLink`). **이후(2026-10-30) 검색(자체 + Windows 검색 연동)·인쇄·폴더 크기 열을 구현했고, 만든 XLSX가 Excel 미리보기 처리기에서 열리는 것도 확인했습니다. 미확인: 실제 셸 덮어쓰기 확인 창, 실제 프린터 출력.** 테스트 514개 통과(전체) |
| 9단계 | ✅ 대부분 완료 (2026-10-29) | 명령 레지스트리(`Command`·`CommandRegistry`: 메뉴·도구 모음·단축키가 같은 명령을 공유, 사용 가능/체크 상태), 메뉴 10개(파일·편집·보기·이동·즐겨찾기·탭·레이아웃·도구·창·도움말)와 도구 모음, 단축키 편집 대화상자(`M-Dir.ini [Shortcuts]`), `IFileOperation` 파일 작업(복사·잘라내기·붙여넣기·삭제·영구 삭제·새 폴더, 다른 패널로 복사/이동), 경로 복사 3종, 여기서 cmd/PowerShell/탐색기 열기, 모두 선택·선택 반전·이름 바꾸기·속성, 전체 화면(F11)·메뉴 막대 숨기기(Alt+F11). **이후 편집 > 실행 취소(셸 `undo` 동사)를 추가했습니다. 미확인: 실제 클립보드·휴지통 삭제·셸 확인 창.** 테스트 298개 통과 |
| 8단계 | ✅ 대부분 완료 (2026-10-29) | 미리보기 창(F12, 오른쪽 패널 + 분할선, 폭 비율 저장): 셸 미리보기 처리기 호스팅(`IPreviewHandler`: PDF·Word·HTML·TXT 확인), 내장 이미지 뷰어(맞춤·확대·회전·EXIF 방향), 내장 텍스트 뷰어(BOM·UTF-8·CP949 감지), 정보 페이지, 150ms 디바운스와 비동기 로드. **이후 미디어 미리보기(WMP), 이미지 이전/다음·슬라이드쇼, 텍스트 줄 바꿈·찾기를 구현했고 Excel·한컴 처리기가 실제로 로드되는 것을 확인했습니다(콜드 스타트 6~10초 멈춤). PowerPoint 처리기는 시험하지 않음.** 테스트 248개 통과 |
| 7단계 | ✅ 완료 (2026-10-29) | `.lnk` 기반 즐겨찾기 저장소(`FavoritesStore`: 하위 폴더, `.order` 순서 파일, 이동·이름 바꾸기·삭제), 즐겨찾기 메뉴(Ctrl+D 추가, Ctrl+클릭·가운데 클릭 = 새 탭), 퀵링크(`Quick-Link` 폴더), 패널별 ★ 버튼, 관리 대화상자(트리, 드래그 순서·이동, F2 이름 바꾸기, 새 폴더, 삭제, 아이콘 변경/초기화, Ctrl+Shift+O). 테스트 193개 통과 |
| 6단계 | ✅ 대부분 완료 (2026-10-29) | `FVO_VISTALAYOUT`(SysListView32 목록), 파일 필터(`ICommDlgBrowser::IncludeObject`: 포함·제외·자동 `*패턴*`·폴더 필터, 패널별 필터 막대 Ctrl+Shift+F, 최근 필터 20개), 색상 필터(DefView 서브클래스 `NM_CUSTOMDRAW`, 규칙 편집 대화상자, Q-Dir `[Filter2]` 가져오기/내보내기, `#DIR`·`#Hidden`·`#BG`·`#BG-A`), 선택·포커스 알림을 `OnStateChange`로 받음(활성 패널 전환에도 사용). **이후 트리 항목 색상, 목록 Shift+휠 가로 스크롤, 정렬 열 강조 끄기를 구현했습니다.** 테스트 160개 통과 |
| 5단계 | ✅ 완료 (2026-10-29), 들여쓰기 옵션 제외 | `FolderTreeControl`(셸 `INameSpaceTreeControl2` 호스팅, 바탕 화면 루트), 패널별 트리(`PaneControl`, 분할선 드래그)와 공용 트리(`MainForm`, 활성 패널을 따라감), 목록↔트리 양방향 동기화, 트리 옵션 4종(연결선·전체 행 선택·한 번 클릭 펼침·현재 폴더 자동 펼침, `M-Dir.ini`의 `[Tree]`), 트리 상태 작업 공간 저장·복원. 보기 메뉴 + F9. 테스트 108개 통과 |
| 4단계 | ⚠️ 일부 완료 (2026-10-29) | 완료: `IniFile`(UTF-16, 원자적 저장), `AppPaths`(포터블 `M-Dir.ini` 감지 / `%APPDATA%\M-Dir`), `AppSettings`, `Workspace`(`.mdr`: 레이아웃·분할 비율·패널별 탭 PIDL·보기 모드·활성 패널·창 위치), `start.mdr` 자동 저장/복원, 파일 메뉴(새로 만들기 Ctrl+N / 열기 Ctrl+O / 저장 Ctrl+S / 다른 이름으로), 명령줄 `.mdr`·폴더 인자. **이후 열·정렬 저장(폴더 종류별)과 Q-Dir `.qdr` 가져오기를 구현했습니다(레이아웃 번호는 0만 대응, 열·정렬 스트림은 가져오지 않음).** 테스트 96개 통과 |
| 3단계 | ✅ 완료 (2026-10-29) | `PaneTab`(탭별 기록·보기 모드), `PaneTabStrip`(직접 그린 탭 막대: 닫기 버튼, 가운데 클릭·더블클릭 닫기, 드래그 순서 변경, "+" 버튼, 여러 줄 모드, 넘칠 때 목록), 주소창 드롭다운(자주 쓰는 폴더 + 드라이브, Ctrl+클릭 = 새 탭), Alt+Enter = 새 탭에서 열기, 탭 메뉴(Ctrl+T/W, Ctrl+Tab, Ctrl+Alt+A, 복제). 테스트 53개 통과 |
| 2단계 | ✅ 완료 (2026-10-29) | `PaneControl`(탐색 막대 + 셸 뷰 + 상태 표시줄, 활성 패널 테두리), `PaneLayout`(레이아웃 13종 분할 트리), `PaneArranger`(배치 계산), `PaneLayoutPanel`(분할선 드래그·더블클릭 초기화), 레이아웃 메뉴(Alt+L, 아이콘), F6/Shift+F6/Ctrl+Alt+1~4. 테스트 43개 통과 |

1단계에서 결정한 사항:
- 탐색 기록은 `EBO_NOTRAVELLOG`로 브라우저 자체 기록을 끄고 `NavigationHistory<ShellLocation>`로 직접 관리합니다(3단계 탭 전환 시 브라우저를 다시 만들어도 기록이 남도록).
- 보기 모드는 `EBO_NOPERSISTVIEWSTATE`로 폴더별 저장을 끄고 패널 단위로 유지합니다(Q-Dir의 `ViewModeN`과 같은 방식).
- 선택 변경 이벤트는 임시로 250ms 간격 폴링을 썼고, 6단계에서 `ICommDlgBrowser::OnStateChange`로 바꿨습니다(항목 수·보기 모드는 알림이 없어 폴링 유지).

2단계에서 결정한 사항:
- 레이아웃은 이진 분할 트리이고, 분할선 번호는 트리 전위 순서입니다. 기본 레이아웃(4 패널 2×2, 세로 분할선 기준)은 분할선 0이 세로, 1·2가 각 열의 가로선이라 Q-Dir의 `SP0`/`SP1`/`SP2`와 대응합니다. 레이아웃을 바꾸면 비율은 그 레이아웃의 기본값(균등 분할)으로 돌아갑니다.
- 레이아웃 번호(1~13)는 M-Dir 자체 번호입니다. Q-Dir `.qdr`의 `ViewMode` 값과의 대응은 확인하지 않았으므로 4단계 `.qdr` 가져오기에서 맞춥니다(`ViewMode=0`이 4분할 기본값인 것만 확인).
- 패널 4개는 항상 만들어 두고 레이아웃에 없는 패널은 숨깁니다. 셸 뷰는 처음 보일 때 생성되고, 숨겨도 폴더와 기록이 유지됩니다.
- 활성 패널은 `IMessageFilter`로 마우스 버튼 메시지를 보고 판정합니다. 셸 뷰 안쪽은 네이티브 창이라 WinForms 포커스 이벤트가 오지 않기 때문입니다. 6단계에서 `ICommDlgBrowser::OnStateChange(CDBOSC_SETFOCUS)`가 들어오면 함께 씁니다.
- 패널 기본 폴더: 1 = 시작 위치(명령줄 인자 또는 내 PC), 2 = 문서, 3 = 다운로드, 4 = 바탕 화면.

3단계에서 결정한 사항:
- 패널당 셸 뷰는 하나만 두고, 탭을 바꾸면 그 탭의 기록·보기 모드를 뷰에 다시 불러옵니다(`ExplorerBrowserHost.LoadState`). 비활성 탭은 PIDL 기록과 보기 모드만 갖습니다(메모리 절약, Q-Dir과 같은 방식). 대신 탭을 바꾸면 폴더를 다시 열기 때문에 스크롤·선택 위치는 유지되지 않습니다.
- 탐색 중에 새 탐색이 오면 셸이 `ERROR_BUSY`(0x800700AA)를 돌려줍니다. 호스트가 가장 최근 요청 하나를 대기열에 두었다가 현재 탐색이 끝나면 실행합니다(이전에는 실패 메시지 상자가 떴음).
- 마지막 탭은 닫히지 않습니다. 탭 더블클릭 = 닫기(Q-Dir 기본값), 빈 곳 더블클릭 = 새 탭.
- 주소창 자동 완성은 1단계의 `SHAutoComplete`를 그대로 씁니다(`IAutoComplete2`로 바꾸는 것과 브레드크럼은 선택 사항이라 미룸).
- 탭 순서·내용의 저장은 4단계(작업 공간)에서 합니다. 탭 그룹, 탭 너비 모드, 기본 탭은 아직 없습니다.

4단계에서 결정한 사항:
- **파일 위치**: exe 옆에 `M-Dir.ini`가 있으면 포터블(모든 파일을 exe 옆에 둠), 없으면 `%APPDATA%\M-Dir`. 자동 저장본은 `start.mdr`. 설정 키는 `[Start] AutoWorkspace`(파일 메뉴에서 켜고 끔).
- **`.mdr` 형식**: UTF-16 INI. `[Workspace]`에 `Layout`(M-Dir 레이아웃 번호), `Ratios`(분할선 비율, 전위 순서), `Active`, `MultilineTabs`, `Window=x;y;w;h|sw=1`(Q-Dir `windowplacement` 표기, 최대화는 `sw=3`), `[Pane1..4]`에 `Tabs`, `Selected`, `TabN`(PIDL을 `%XX`로 인코딩, Q-Dir와 같은 방식), `TabN.ViewMode`, `TabN.IconSize`. 레이아웃에 없는 패널(숨김)의 탭도 저장합니다.
- **손상된 파일에 강함**: 알 수 없는 레이아웃·비율 개수 불일치·형식이 깨진 PIDL·너무 작은 창 크기는 기본값으로 대체하고, 쓸 수 있는 탭이 없는 패널은 "내 PC" 탭 하나로 시작합니다. PIDL은 구조(크기 필드와 끝 표시)를 검사한 뒤에만 셸에 넘깁니다.
- **삭제된 폴더**: 복원할 때 폴더가 없으면 존재하는 가장 가까운 상위 폴더로 대체합니다(네트워크 경로와 가상 폴더는 검사하지 않고 그대로 둠 — 응답 없는 서버에서 멈추지 않도록).
- **창 위치**: 저장된 위치가 현재 어느 모니터와도 겹치지 않으면(모니터를 뺀 경우) 무시합니다. 파일에서 작업 공간을 열 때는 창 위치를 바꾸지 않습니다. WinForms `Form.RestoreBounds`는 한 번도 옮기지 않은 창에서 `-1,-1`을 돌려주므로 보통 상태에서는 `Bounds`를 씁니다(실제 exe 실행에서 발견해 고침).
- **명령줄**: `.mdr` 파일을 주면 그 작업 공간을 열고, 폴더를 주면 저장된 작업 공간 위에 1번 패널로 엽니다.
- **보류한 것과 이유** (4단계):
  - 열·정렬 저장: `EBO_NOPERSISTVIEWSTATE` 때문에 셸이 폴더마다 정렬을 기억하지 않으므로, Q-Dir처럼 **폴더 종류(CLSID)별 보기 상태 저장소**(`Spatlen_` 키, `IFolderView2.GetSortColumns`/`IColumnManager`)를 따로 설계해야 합니다. 탭 단위로 저장하면 폴더를 옮길 때 엉뚱한 정렬이 적용됩니다.
  - `.qdr` 가져오기: Q-Dir `ViewMode` 값과 M-Dir 레이아웃 번호의 대응표를 아직 확인하지 못했습니다(`ViewMode=0`이 4분할 기본값인 것만 확인).

5단계에서 결정한 사항:
- **호스팅**: `CLSID_NamespaceTreeControl`(`explorerframe.dll`)을 `INameSpaceTreeControl2`로 호스팅합니다(기본 인터페이스의 vtable을 펼쳐서 선언). 스타일 기본값은 Q-Dir와 같은 `HASEXPANDOS|HORIZONTALSCROLL|SHOWSELECTIONALWAYS|TABSTOP`(+`HASLINES`)이고, Q-Dir 초기화 함수(`0x460bb6`)가 쓰는 값 `0x200A3`과 일치함을 확인했습니다. 루트는 네임스페이스 루트(바탕 화면)를 펼친 상태로 두어 탐색기 탐색 창과 같은 모양이 됩니다. 컨텍스트 메뉴·아이콘은 `E_NOTIMPL`을 돌려줘 셸 기본 동작을 씁니다.
- **동기화 규칙**: `SelectLocation`(목록→트리)은 사용자 이벤트(`LocationSelected`)를 일으키지 않고, 트리에서 마우스·키보드로 고른 경우에만 이벤트가 나갑니다. 같은 위치의 늦게 도착한 알림은 위치 비교로 버립니다(되돌림 루프 방지). 트리에서 빠르게 방향키를 누르면 3단계의 `ERROR_BUSY` 대기열이 마지막 이동만 실행합니다.
- **두 모드**: 패널별 트리는 켜면 처음 한 번만 만들고(안 쓰는 패널은 셸 컨트롤을 만들지 않음) 숨겨도 상태를 유지합니다. 공용 트리는 활성 패널을 따라가고, 트리에서 고르면 활성 패널이 이동합니다. 두 모드는 각자의 켬/끔 상태를 따로 저장해서 모드를 오가도 잃지 않습니다.
- **저장**: `.mdr`의 `[Workspace]`에 `TreeMode`(0 패널별, 1 공용 — Q-Dir `tree_mode`의 의미와 같은지는 확인하지 못함), `SharedTree`, `SharedTreeSplit`, 각 `[PaneN]`에 `Tree`, `TreeSplit`(1/10000, 기본 2500 = Q-Dir `TreeNsplit` 기본값). 옵션 4종은 작업 공간이 아니라 `M-Dir.ini`의 `[Tree]`에 둡니다(모든 트리 공통 설정).
- **보류**: 들여쓰기 옵션(내부 `SysTreeView32`에 `TVM_SETINDENT`를 보내야 함), 트리 항목 색상(6단계 색상 필터에서 `TVM_GETITEMRECT` + `HitTest`로 함, 분석 1.6 참고), 트리 안에서의 파일 작업·드래그 동작 점검(9단계).

6단계에서 결정한 사항:
- **목록 레이아웃**: `IExplorerBrowser.Initialize` 전에 `IFolderViewOptions.SetFolderViewOptions(FVO_VISTALAYOUT)`를 지정해 목록을 `SysListView32`로 만듭니다. 보기 프리셋 8종(큰 아이콘~내용)은 모두 셸이 받아들이고 그대로 보고합니다(내용 보기의 실제 모양은 확인하지 않음). 목록 모양은 Windows 11의 DirectUI 목록과 조금 다릅니다(Q-Dir도 같은 모양).
- **필터 후크**: 브라우저에 `IObjectWithSite` 사이트를 달고, 셸 뷰가 `IServiceProvider`로 `ICommDlgBrowser`(2·3 포함)를 요청하면 돌려줍니다. 셸은 서비스 ID로 `ICommDlgBrowser`의 IID를 쓰고, **항목마다 한 번씩** 요청합니다. 호출은 UI 스레드에서 왔지만 필터 대리자는 불변으로 바꿔 치우고 폴더 캐시는 락으로 보호합니다. 필터가 비어 있으면 COM 호출 없이 바로 통과시킵니다.
- **`CDBOSC_*` 값**: `SETFOCUS=0`, `KILLFOCUS=1`, `SELCHANGE=2`, `RENAME=3`, `STATECHANGE=4`. (처음에 SELCHANGE를 1로 잘못 알고 있었고, 상태 로그를 찍어 보고서야 바로잡았습니다.) 선택 변경은 즉시 반영하고, **항목 수·보기 모드는 셸이 알림을 주지 않아 250ms 폴링을 그대로 둡니다.**
- **필터 의미(Q-Dir 0x456a3f/0x456b2b 기준)**: `;`·`,`로 구분한 와일드카드, 이름은 대소문자 무시. 패턴이 **하나이고 와일드카드가 없을 때만** `*패턴*`으로 감쌉니다. 제외(반전) 옵션. 폴더는 "폴더도" 옵션이 켜져 있을 때만 필터링합니다. 필터 막대를 닫으면 필터도 꺼집니다(보이지 않는 활성 필터가 없도록). 필터는 패널 단위로 모든 탭·폴더에 적용되고 작업 공간(`Filter`·`FilterNot`·`FilterAuto`·`FilterDirs`·`FilterBar`)에 저장됩니다.
- **색상 규칙 의미(Q-Dir 0x46d863 기준)**: 규칙을 위에서부터 평가하며 속성(글자색·배경색·굵게)마다 **처음 값을 준 규칙**이 이깁니다(`-1` = 미설정). 그래서 `*` 같은 규칙을 맨 아래 두어도 위의 규칙을 덮지 않습니다. 선택된 행은 시스템 선택 색을 유지합니다(`CDIS_SELECTED`는 모든 행에 켜져 있어 믿을 수 없어서 `LVM_GETITEMSTATE`로 확인).
- **색상 규칙 파일 형식**: M-Dir은 `M-Dir.ini`의 `[ColorFilter]`(`RuleN=패턴=사용=글자색=배경색=굵게`, 색은 `RRGGBB` 또는 `-1`)에 저장합니다. Q-Dir `[Filter2]`는 가져오기·내보내기 때 **패턴·사용·글자색만** 해석합니다. 뒤쪽 필드 4개(`-1=1=-1=0`)의 뜻을 디컴파일로 확정하지 못했고, 색 바이트 순서도 파서 함수를 확인하지 못해 `RRGGBB`(`FF0000`=빨강)로 가정했습니다. Q-Dir의 `*=0000BB` 전체 파일 파란색 규칙은 기본값에서 뺐습니다.
- **`#BG`/`#BG-A`**: 목록 배경색(비활성/활성 패널)을 `LVM_SETBKCOLOR`로 적용합니다. 규칙의 글자색 칸이 배경색입니다.
- **테스트 안정성**: xUnit이 테스트 클래스를 병렬로 돌리면서 여러 UI 스레드가 동시에 컨트롤을 만들 때 테스트 호스트가 한 번 `InvalidOperationException`(비동기 컬렉션 동시 수정)으로 중단됐습니다. WinForms 내부의 스레드 안전하지 않은 정적 상태(예: 모든 `ToolStrip` 목록)가 원인이라는 추정입니다(증명하지는 못함). `UiTest`가 UI 스레드를 한 번에 하나씩만 돌리도록 직렬화한 뒤 10회 연속 통과했습니다(전체 약 16초 → 30초). 실제 앱은 UI 스레드가 하나입니다.
- **보류**: 트리 항목 색상(`INameSpaceTreeControl::HitTest`로 항목을 찾아 `NM_CUSTOMDRAW` 처리), 목록 Shift+휠 가로 스크롤, 정렬 열 강조 끄기(`LVM_SETSELECTEDCOLUMN(-1)`).

7단계에서 결정한 사항:
- **저장 방식**: 즐겨찾기는 데이터 폴더의 `Favorites\` 아래 표준 `.lnk` 바로 가기이고, 하위 폴더가 메뉴 계층입니다(Q-Dir의 `Favoriten\`·탐색기 즐겨찾기와 같은 방식이라 파일을 그대로 복사해 쓸 수 있음). 대상은 경로가 아니라 **PIDL**(`IShellLink::SetIDList`)로 저장해서 "내 PC" 같은 가상 폴더도 즐겨찾기가 됩니다. 다른 프로그램이 만든 바로 가기는 PIDL이 없으면 경로(`GetPath`)로 읽습니다.
- **순서**: 파일 시스템에는 순서가 없어서 폴더마다 숨김 `.order` 텍스트 파일(한 줄에 항목 이름 하나)에 저장합니다. 목록에 없는 항목은 퀵링크 폴더 → 폴더 → 바로 가기, 이름 순으로 뒤에 붙습니다. 모든 작업은 즐겨찾기 폴더 밖으로 나가지 못하도록 경로를 검사합니다(`..` 포함).
- **퀵링크**: `Quick-Link` 폴더는 항상 있고 이름 바꾸기·이동·삭제가 막혀 있으며 메뉴에서는 "퀵링크"로 보입니다. 별도 도구 모음 대신 즐겨찾기 메뉴의 첫 항목으로만 구현했습니다.
- **메뉴**: 폴더 항목은 하위 메뉴가 열릴 때 채웁니다(큰 트리도 열기 전에는 비용 없음). 고정 항목(추가, 정리)은 단축키가 닫힌 메뉴에서도 동작하도록 한 번만 만들고, 아래 목록만 열 때마다 다시 만듭니다. 대상을 읽을 수 없는 바로 가기는 비활성으로 표시합니다.
- **COM 해제**: `IShellLink`를 불러오면 COM 객체가 해제될 때까지 파일이 열려 있습니다. 가비지 컬렉터를 기다리면 바로 이어지는 삭제·이름 바꾸기가 실패하므로 `FinalReleaseComObject`로 즉시 해제합니다.
- **일시적 파일 잠금**: 즐겨찾기가 즐겨찾기 폴더를 **포함하는** 폴더(예: `AppData`)를 가리키면, 그 폴더의 아이콘을 읽을 때 셸이 폴더를 훑으면서 잠시 파일을 잡습니다. 이름 바꾸기·이동·삭제는 공유 위반일 때 짧게(최대 8회) 다시 시도합니다.
- **`.order` 숨김 속성**: 숨김 파일은 덮어쓸 수 없어서 쓰기 전에 속성을 지우고 쓴 뒤 다시 숨깁니다(처음에는 두 번째 저장부터 `UnauthorizedAccessException`이 났음).
- **미확인/보류**: 아이콘 선택 대화상자(`shell32` 서수 62 `PickIconDlg`)는 모달이라 테스트하지 못했고 코드 경로만 연결했습니다(아이콘 저장·읽기·표시는 테스트함). 즐겨찾기 폴더의 "모두 탭으로 열기", Q-Dir 즐겨찾기 폴더 가져오기 메뉴, 시작 시 펼침 옵션은 없습니다. 메뉴 항목의 Ctrl+클릭 자체는 실제 마우스로 시험하지 않았고 `newTab` 경로만 테스트했습니다.

8단계에서 결정한 사항:
- **무엇을 어떻게 보여 주나**: 이미지(GDI+가 읽는 형식)와 알려진 텍스트 확장자는 **내장 뷰어**, 그 밖의 형식은 **셸 미리보기 처리기**(탐색기 미리보기 창과 같은 확장), 처리기가 없으면 내용이 텍스트인지 앞부분을 읽어 판별해서 텍스트로, 아니면 **정보 페이지**(이름·종류·크기·수정일·위치)를 보여 줍니다. 폴더는 항목 수가 있는 정보 페이지, 여러 개를 고르면 "N개 항목이 선택되었습니다." 안내입니다. 이 PC에는 이미지·Markdown·JSON·소스 파일용 처리기가 없어서(`AssocQueryString`로 확인) 내장 뷰어가 필요합니다. `.txt`는 처리기가 있지만 인코딩 감지와 고정폭 글꼴을 위해 내장 텍스트 뷰어를 우선합니다.
- **처리기 호스팅**: `AssocQueryString(ASSOCSTR_SHELLEXTENSION, {8895b1c6…})`로 CLSID를 찾고 `CoCreateInstance`를 **`CLSCTX_LOCAL_SERVER` 먼저**(처리기가 `prevhost.exe` 대리 프로세스에서 돌아 이 프로그램을 죽일 수 없음, 탐색기와 같은 방식), 실패하면 인프로세스로 만듭니다. 초기화는 **스트림 → 셸 항목 → 파일 경로** 순으로 시도합니다(대리 프로세스 처리기는 스트림을 기대함). 해제는 `Unload` 후 `FinalReleaseComObject`로 즉시 하며, 숨기거나 다른 파일로 바꾸면 처리기가 잡고 있던 파일이 풀리는 것을 테스트로 확인했습니다.
- **실제 처리기 확인**: 이 PC에서 PDF(`PDF Preview Handler`), HTML(`CLSID_PreviewHtml`), Word(`Microsoft Word previewer`), TXT가 실제 내용을 그렸습니다. 처리기 호출은 UI 스레드에서 동기로 부르고(COM STA 요건), 따뜻한 상태에서는 100~460ms 걸렸습니다. **Office 처리기를 처음 띄우는 콜드 스타트는 측정하지 못했고, 그동안 창이 멈출 수 있습니다.**
- **이미지 뷰어**: 파일은 64MB까지만 읽고(그 이상은 정보 페이지), 파일을 잠그지 않으며(`FileShare.ReadWrite|Delete`), EXIF 방향(0x0112)을 적용합니다. 마우스 휠 확대·드래그 이동·더블클릭 맞춤/100% 전환, 회전은 화면에서만 바꿉니다(파일은 건드리지 않음). 맞춤은 줄이기만 하고 늘리지 않습니다. WebP는 GDI+가 읽지 못해 처리기/정보 페이지로 갑니다.
- **텍스트 뷰어**: 앞 256KB만 읽습니다. BOM(UTF-8/UTF-16) → 엄격한 UTF-8 → CP949 순으로 판별하고, 읽기 한도가 UTF-8 문자 중간에서 끊겨도 한글 코드 페이지로 오판하지 않도록 끝의 불완전한 시퀀스를 자릅니다. NUL이나 제어 문자가 많으면 이진 파일로 봅니다.
- **갱신 규칙**: 활성 패널의 선택이 바뀔 때(선택 알림 `CDBOSC_SELCHANGE`와 폴더 이동 직후) 150ms 뒤에 한 번만 미리봅니다. 더 새 선택이 오면 로드 중이던 이전 것은 버립니다(버전 번호). 패널을 숨기면 타이머를 끄고 처리기를 해제합니다. 활성 패널을 바꾸면 그 패널의 선택으로 바뀝니다.
- **배치와 저장**: 창 오른쪽 패널이고 폭은 창 너비의 비율(기본 30%, 1/10000 단위)로 `.mdr`의 `Preview`·`PreviewSplit`에 저장합니다. Q-Dir의 `Vorschau`·`VorschauPos`와 키 이름은 다릅니다.
- **보류**: 동영상·음악 미리보기(원본은 WMP 컨트롤을 씀; 처리기가 없는 형식은 정보 페이지로 갑니다), 이미지 슬라이드쇼·이전/다음, 텍스트 뷰어의 줄 바꿈·검색, 미리보기 창 안의 키보드 포커스 처리, Excel·PowerPoint·한컴(HWP) 처리기 실제 확인(등록은 확인했고 로드는 시험하지 않음).

9단계에서 결정한 사항:
- **명령 시스템**: 모든 동작을 한 번 등록(`id`, 메뉴 글자, 기본 단축키 목록, 사용 가능 조건, 체크 상태, 도구 모음 글리프)하고 메뉴 항목·도구 모음 버튼·키 입력이 같은 명령을 실행합니다. 메뉴는 열 때, 도구 모음은 선택·폴더 이동·클립보드 변화(400ms 폴링)에 맞춰 사용 가능/체크 상태를 갱신합니다. `MainForm`은 창 로직(`MainForm.cs`)과 명령·메뉴(`MainForm.Commands.cs`)로 나눴습니다.
- **키 처리는 한 곳**: 메뉴 항목의 `ShortcutKeys`를 쓰지 않고 `ProcessCmdKey`에서 레지스트리로 찾아 실행합니다(메뉴에는 `ShortcutKeyDisplayString`으로 표시만). 사용할 수 없는 명령의 키는 삼키지 않고 포커스된 컨트롤에 넘깁니다. **셸 뷰나 텍스트 상자가 이미 처리하는 기본 키(Ctrl+C/X/V/A, Delete, Shift+Delete, F2, Alt+Enter, Alt+F4)는 가로채지 않습니다**(`NativeShortcut`). 주소창·필터 상자에서 Ctrl+C가 글자를 복사해야 하기 때문입니다. 사용자가 그 명령에 다른 키를 지정하면 그 키는 가로챕니다.
- **단축키 편집**: 명령마다 기본 키 목록이 있고(예: 주소창 F4·Alt+D·Ctrl+L) 사용자가 바꾸면 그 명령의 키가 지정한 한 개로 바뀝니다. 설정에는 **바뀐 것만** 저장해서(`[Shortcuts] 명령id=Ctrl+Alt+K`, 빈 값 = 단축키 없음) 이후 버전에서 기본값이 바뀌어도 건드리지 않은 사용자는 새 기본값을 받습니다. 다른 명령이 쓰는 키(셸 기본 키 포함)를 지정하면 확인을 묻고 그 명령에서 뺍니다. 기본 키들이 서로 겹치지 않는지 테스트로 확인합니다.
- **파일 작업**: `IFileOperation`(진행률 창·이름 충돌 질문·휴지통·실행 취소 기록은 셸이 처리; 복사·이동은 `FOFX_ADDUNDORECORD`)을 씁니다. 삭제는 기본이 휴지통이고, 영구 삭제는 `FOF_WANTNUKEWARNING`으로 셸이 확인합니다. 항목은 `SHCreateItemFromParsingName`으로 경로 그대로 만들어서 `%` 가 들어간 파일 이름이 환경 변수로 바뀌지 않습니다(`ShellLocation.Parse`는 바꿈). 복사한 파일은 탐색기와 같은 형식(`Preferred DropEffect`)으로 클립보드에 넣어 탐색기와 서로 붙여넣을 수 있습니다.
- **파일 작업은 별도 STA 스레드에서**(`FileOperations.InBackground`): `PerformOperations()`가 끝날 때까지 돌아오지 않고 소유 창을 비활성화해서, 예전에는 큰 폴더를 지우는 동안 창 전체가 멈췄습니다. 삭제·붙여넣기·다른 패널로 복사/이동은 이제 소유 창 없이 작업 스레드에서 돌고(진행률 창은 셸이 그대로 띄움), 끝나면 UI 스레드에서 오류 보고·새로 고침을 합니다. 진행 중에 닫으면 확인을 묻습니다(`RunningFileOperations`). 목록에 포커스가 있을 때의 Delete/Shift+Delete는 셸이 자기 스레드에서 처리하던 것을 `TryDeleteKey`가 가로채 같은 경로로 보냅니다(이름 바꾸기 상자·텍스트 상자는 제외). **미확인/한계: 셸 우클릭 메뉴의 "삭제"는 여전히 셸이 UI 스레드에서 처리해 멈춥니다. 새 폴더·폴더 비교의 복사는 동기입니다. 실제로 수십만 개를 지우며 창을 쓰는 것은 직접 확인하지 못했습니다(자동 시험은 스레드가 다르고 STA인지만 봄).**
- **"다른 패널로"**: 다음 보이는 패널로 복사·이동(Ctrl+Alt+C / Ctrl+Alt+M)과, 패널 1~4를 직접 고르는 항목이 있습니다. 대상이 활성 패널이거나 레이아웃에 없거나 파일 시스템 폴더가 아니면 비활성입니다.
- **새 폴더**: 이름을 미리 번호까지 정해서(`새 폴더`, `새 폴더 (2)`) 만들고, 뷰에 나타나면(최대 3초 기다림) 선택한 뒤 이름 바꾸기를 시작합니다.
- **경로 복사**: 선택 항목(없으면 현재 폴더)의 전체 경로·파일 이름·폴더 경로를 줄마다 하나씩 텍스트로 복사합니다. "여기서 열기"는 새 창에서 cmd·PowerShell을 현재 폴더에서, 탐색기는 경로를 인자 하나로 넘겨 엽니다(공백·따옴표가 인자를 늘리지 못함).
- **시험 방식**: 클립보드와 프로세스 실행은 인터페이스(`IClipboardService`, `IProcessLauncher`)로 분리해 테스트가 사용자의 클립보드를 건드리거나 창을 띄우지 않습니다. 파일 작업 테스트는 조용한 모드(대화상자 없음, 이름 충돌 시 번호)와 영구 삭제로 돌립니다.
- **보류/미확인**: 실제 시스템 클립보드 왕복, 휴지통으로 보내는 삭제와 셸 확인 대화상자, "실행 취소" 메뉴(Ctrl+Z는 셸 뷰가 처리), 단축키 대화상자의 실제 키 입력 캡처, 전체 화면의 실제 모양은 시험하지 않았습니다. 도구 모음 구성 편집은 11단계, 하드 링크·폴더 크기 등은 10단계입니다.

10단계에서 결정한 사항:
- **핵심은 화면과 분리**: 해시·내보내기·이름 바꾸기·비교·크기 계산은 화면 없는 클래스(`Features/`)로 만들어 실제 파일 시스템에서 직접 시험하고, 대화상자(`HashDialog`·`MultiRenameDialog`·`CompareDialog`)와 `MainForm.Tools.cs`의 명령은 그 위에 얹었습니다. 대화상자는 `RunDialog`, 저장 경로 선택은 `ExportTargetPicker`·`LinkPathPicker`로 바꿔 끼울 수 있어 시험이 창을 막지 않습니다.
- **폴더 크기**: 정션·심볼릭 링크는 따라가지 않고(무한 순환 방지, 링크된 데이터는 폴더 "안"이 아님) 읽지 못한 곳이 있으면 "이상"으로 표시합니다. 결과는 5분간 캐시하고 불완전한 결과는 캐시하지 않습니다. 선택이 없으면 현재 폴더를 잽니다. 진행 보고는 UI 스레드로 늦게 도착할 수 있어, 끝난 뒤에는 최종 값을 덮어쓰지 못하게 막았습니다(시험이 실제로 잡은 경쟁 상태).
- **내보내기**: 선택이 있으면 선택 항목, 없으면 목록에 보이는 모든 항목(필터 적용 후, `IFolderView2.Items(SVGIO_ALLVIEW)`). 텍스트·CSV는 UTF-8(BOM) 기본, HTML은 항상 UTF-8, XLSX는 최소 OOXML(zip)을 직접 씁니다.
- **이름 바꾸기**: `[N] [E] [C] [C:3] [D] [P] [[` 마스크. 잘못된 이름·겹치는 새 이름·이미 있는 이름은 빨갛게 표시하고 그 항목만 건너뜁니다. 이름을 서로 바꾸거나 한 칸씩 밀리는 번호(1→2, 2→3)는 임시 이름을 거쳐 두 단계로 적용하고, 마지막 적용은 되돌릴 수 있습니다.
- **폴더 비교/동기화**: 비교는 활성 패널과 다음 보이는 패널의 폴더. 빠른 비교는 크기+수정 시각(2초 오차), 내용 비교는 크기가 같은 쌍만 SHA-256. 동기화는 **절대 삭제하지 않고**, 양쪽이 모두 바뀐 충돌(내용 다름)이나 종류가 다른 쌍(폴더↔파일)은 건드리지 않습니다. 복사는 `IFileOperation`이라 덮어쓰기 질문은 셸이 합니다(선택 항목을 한 방향으로 강제 복사하는 것은 사용자가 고른 항목만).
- **해시**: 1MB씩 비동기로 읽어 큰 파일도 되고 파일을 잠그지 않습니다(`FileShare.ReadWrite|Delete`). 붙여넣은 글에서 해시를 찾는 규칙은 "영숫자로 둘러싸이지 않은 16진 문자열", 그것이 없으면 글 전체가 16진 쌍일 때(공백·콜론 구분). 목록은 `sha256sum` 형식(`해시 *이름`)으로 복사됩니다. 하드 링크는 같은 볼륨·NTFS만(`CreateHardLinkW`), 이름 제안은 "x - 링크.ext".
- **(당시 보류, 아래 "10단계 이후 구현한 것"에서 구현함)**: Windows 검색(`search-ms:`) 연동과 자체 검색, 인쇄·인쇄 미리보기, 폴더 크기를 목록 열로 보여 주기. **미확인**: XLSX를 Excel에서 열어 본 적 없음, 실제 셸 덮어쓰기 확인 창과 진행률 창, 수만 개 항목에서의 속도.

11단계에서 결정한 사항 (시스템 통합 & 사용자화, 2026-10-30):
- **명령줄**(`StartupArguments`): `M-Dir.exe [옵션] [폴더 | 작업공간.mdr]`. `-explore`(또는 `/e`)는 폴더 트리를 함께 보여 줍니다(탐색기 `/e`와 같은 뜻으로 정함, Q-Dir 쪽 의미는 확인 못 함). `-new`는 단일 인스턴스 옵션이 켜져 있어도 새 창, `-minimized`는 최소화(트레이 옵션이 켜져 있으면 트레이)로 시작. 알 수 없는 옵션은 무시하고, 첫 폴더/첫 작업 공간만 씁니다. `/data/sub`처럼 슬래시 뒤에 경로 구분자가 있으면 옵션이 아니라 경로입니다.
- **단일 인스턴스**(`SingleInstance`): `Local\M-Dir-instance` 뮤텍스 + `CurrentUserOnly` 명명된 파이프. 두 번째 프로세스는 인자(상대 경로는 절대 경로로 바꿔서)를 한 줄에 하나씩 보내고 끝납니다. 받은 쪽은 폴더를 **활성 패널의 새 탭**으로 열고(작업 공간은 배치를 교체), 창을 앞으로 가져옵니다. 3초 안에 응답이 없으면(멈춘 첫 인스턴스) 그냥 새 창을 띄웁니다. 설정 `[Start] SingleInstance`, 기본 꺼짐, 켜고 끈 뒤 다시 시작해야 적용. **자체 포함 단일 exe로 실제 두 번 실행해서 확인함**(두 번째 프로세스가 끝나고 첫 창이 새 폴더를 열었음).
- **트레이**: `[Start] MinimizeToTray`. 최소화하면 `Hide()` + `NotifyIcon`, 아이콘 왼쪽 클릭 = 복원, 메뉴 = 열기/끝내기. 트레이 아이콘은 exe의 연결된 아이콘(아이콘 리소스가 없어서 기본 아이콘)을 씁니다.
- **자동 시작·탐색기 메뉴**(`ShellRegistration`): HKCU만 씁니다. 자동 시작은 `Run\M-Dir = "exe" -minimized`, 탐색기 메뉴는 `Directory\shell`, `Directory\Background\shell`(%V), `Drive\shell` 아래 `M-Dir\command`. **포터블 모드(exe 옆 `M-Dir.ini`)에서는 두 명령이 꺼져 있어** 레지스트리에 아무것도 쓰지 않습니다. 시험은 `HKCU\Software\MDirTests\<guid>` 아래에서 하고 지웁니다. **실제 탐색기 우클릭 메뉴가 뜨는 것은 확인하지 않았습니다.**
- **다크 모드**: `Application.SetColorMode`(.NET 10, 실험 API `WFO5001`)로 WinForms 컨트롤을, `DwmSetWindowAttribute(20)`으로 제목 표시줄을, 목록은 `SetWindowTheme("DarkMode_Explorer")` + `LVM_SETBKCOLOR/TEXTCOLOR`, 열 머리글은 `DarkMode_ItemsView`, 트리는 `INameSpaceTreeControl2.SetTheme` + `TVM_SETBKCOLOR/TEXTCOLOR`로 칠합니다. 설정 `[Appearance] ColorMode` = System/Light/Dark, 바꾸면 곧바로 적용됩니다. **다크에서는 정렬 열 강조(밝은 줄무늬)를 끕니다**(`LVM_SETSELECTEDCOLUMN(-1)`, 셸이 정렬 때마다 다시 지정하므로 그릴 때마다 지움). 스크린샷으로 확인했고, 머리글 글자색 대비는 낮은 편입니다. 시스템 설정 따르기는 프로필이 있을 때만(시험이 항상 밝게 돌도록).
- **UI 배율**: 100/125/150/175/200%. 창 글꼴을 키우면 탭·주소창·상태 표시줄이 따라가고, 메뉴·도구 모음은 글꼴을 따로 가지므로 함께 지정합니다. **셸 파일 목록의 글꼴은 키우지 않습니다**(목록은 보기 모드의 아이콘 크기로 조절).
- **도구 모음 편집**: 명령 id와 `-`(구분선), `@layouts`(레이아웃 드롭다운) 토큰 목록을 `[Toolbar] Items`에 저장합니다(기본 도구 모음이면 저장하지 않아 이후 기본값 변경을 받음). 알 수 없는 명령·중복·맨앞/맨뒤/연속 구분선은 걸러냅니다. 전체 화면(F11)은 9단계에서 이미 구현.
- **`.NET` 함정**: `ToolStrip.Items`를 돌면서 항목을 `Dispose()`하면 컬렉션이 바뀌어 예외가 납니다(복사본을 돌 것).

12단계에서 결정한 사항 (다국어 & 배포, 2026-10-30):
- **문자열 방식(계획의 `.resx`와 다르게 함)**: 프로그램의 한국어 문구가 곧 키입니다(`Loc.T("파일(&F)")`). 영어는 `Localization/EnglishStrings.cs`에 내장(약 400개), 없는 문구는 한국어 그대로 나옵니다. 언어 파일 `Lang\<코드>.lng`(UTF-8, `한국어 원문 TAB 번역`, `\n` 줄 바꿈, `#` 주석)을 exe 옆이나 데이터 폴더에 두면 창 > 언어 메뉴에 나타나고, 파일에 없는 문구는 영어로 대체됩니다. `.resx`를 쓰지 않은 이유: 한국어 원문을 키로 쓰는 쪽이 코드 수정이 적고, 외부 언어 팩을 컴파일 없이 추가할 수 있습니다.
- **런타임 전환**: 명령(`Command.Text`는 번역된 값을 돌려줌)·메뉴 막대(다시 만듦)·도구 모음·패널의 고정 문구(`Loc.Apply`가 컨트롤 트리를 돌며 첫 한국어 문구를 기억했다가 양방향으로 번역)·대화상자(열 때 번역)가 바로 바뀝니다. 설정 `[Appearance] Language`, 기본값은 한국어 Windows면 `ko`, 아니면 `en`.
- **번역하지 못한 것**: 오류 메시지 일부와 대화상자 안의 동적 문장(비교·이름 바꾸기·해시 요약 등)은 한국어로 남습니다. 열 머리글·종류 이름 같은 셸 문구는 Windows 표시 언어를 따릅니다. 전역 상태라서 언어를 바꾸는 시험은 UI 게이트 안에서 돌리고 끝에 한국어로 되돌립니다.
- **배포**: `publish.ps1` = `dotnet publish -c Release -r win-x64`두 가지: 자체 포함 단일 파일(압축, **47MB**, .NET 설치 불필요)과 프레임워크 종속 단일 파일(`M-Dir-lite`, **0.7MB**, .NET 10 데스크톱 런타임 필요). **트리밍은 쓰지 않음**: SDK가 Windows Forms 트리밍을 막고(`NETSDK1175`) 이 프로그램의 COM 인터롭·`dynamic`이 리플렉션에 기대기 때문. 각각 포터블 zip을 만들고 두 exe 모두 실행해 창이 뜨고 정상 종료되는 것을 확인함(`M-Dir.exe` + 빈 `M-Dir.ini` + `Lang\README.txt`). `M-Dir.ini` 파일이 있으면 설정·즐겨찾기·작업 공간을 모두 exe 옆에 둡니다. **설치 프로그램은 만들지 않았습니다**(계획에서도 선택 사항).

10단계 이후 구현한 것 (2026-10-30):
- **검색**(`FileSearcher`·`SearchDialog`, Ctrl+F/F3): 이름(와일드카드, 낱말 하나면 `*낱말*`)과 내용(텍스트 파일만, 4MB 이하, BOM/UTF-8/CP949 판별), 하위 폴더·대소문자·폴더 포함, 결과 10,000개 제한, 취소 가능, 링크는 따라가지 않음. 결과 더블클릭 = 활성 패널에서 그 폴더를 열고 항목 선택. **Windows 검색 연동**: `search-ms:` 주소는 `IExplorerBrowser`가 열지 못해서(탐색 실패) **저장된 검색(`.search-ms` XML 파일, `%TEMP%\M-Dir`)을 만들어 그 파일로 이동**하는 방식으로 했고, 실제로 결과가 목록에 나옵니다. 일주일 지난 임시 검색 파일은 지웁니다.
- **인쇄**(`ListPrinter`, Ctrl+P): 목록(선택이 있으면 선택)을 제목·표·쪽 번호로 인쇄하고 인쇄 미리 보기도 있습니다. 쪽 계산과 그리기는 `Graphics`에 직접 그려 시험합니다. 실제 프린터로 출력해 보지는 않았습니다.
- **폴더 크기 열**(`FolderSizeColumnService` + `ColorListHook`): 크기 열의 비어 있는 폴더 칸에 크기를 그립니다(`NM_CUSTOMDRAW`의 `CDDS_ITEMPOSTPAINT`). 크기 열은 열 머리글을 **속성 시스템의 현지화된 이름**(`PSGetPropertyDescription`)과 비교해 찾습니다. 백그라운드 2개로 계산하고 상태 표시줄과 같은 캐시를 씁니다(읽지 못한 곳이 있으면 `+`). 폴더를 옮기면 대기 작업을 취소합니다. 기본은 꺼짐(보기 메뉴), 폴더가 많으면 계산 부하가 있습니다.
- **열·정렬 저장**: 폴더 **종류**(셸 폴더의 CLSID)별로 열(순서·너비, `IColumnManager`)과 정렬(`IFolderView2.Get/SetSortColumns`)을 패널 단위로 기억해 같은 종류 폴더를 열 때 적용하고 `.mdr`(`ViewsN`)에 저장합니다. **사용자가 바꾼 경우에만** 저장하므로(열 때의 상태와 비교) Downloads 같은 폴더의 셸 기본값이 다른 폴더로 퍼지지 않습니다. 셸이 채우는 중에 정렬을 덮어써서 탐색 이벤트 밖(메시지 루프가 한가할 때)에서 적용합니다. `IColumnManager.SetColumns`의 배열 크기 지정이 잘못돼서(`SizeParamIndex`) 적용이 조용히 실패하던 것을 시험이 잡았습니다. `GetAllItemPaths`는 화면 순서(`IFolderView2.Item(i)`)로 바꿨습니다(`SVGIO_ALLVIEW` 배열은 정렬을 따르지 않음 — 내보내기·인쇄 순서가 이제 화면과 같습니다).
- **`.qdr` 가져오기**(`QdrImporter`, 파일 > Q-Dir 작업 공간 가져오기): 패널 폴더(PIDL), 보기 모드, 분할선(SP0~2), 트리, 미리보기, 활성 패널, 창 위치를 가져옵니다. **Q-Dir 레이아웃 번호는 0(기본 4분할)만 알아서 다른 값은 기본 4분할로 열고 안내**하며, 탭과 열·정렬(Q-Dir 전용 `IPropertyBag` 스트림)은 가져오지 않습니다. 실제 `D:\Q-Dir\start.qdr`로 시험했습니다.
- **즐겨찾기**: 폴더 항목 Ctrl+클릭 = 안의 바로 가기를 모두 새 탭으로 열기, 즐겨찾기 > Q-Dir 즐겨찾기 폴더 가져오기(`.lnk`와 하위 폴더 복사, `Quick-Link`는 병합). 시작 시 펼침 옵션은 만들지 않았습니다.
- **미리보기**: 미디어(동영상·음악) = Windows Media Player ActiveX(`MediaView`, 자동 재생 안 함, 이 PC에서 WAV로 확인; WMP가 없으면 정보 페이지), 이미지 이전/다음(◀ ▶)과 슬라이드쇼(3초), 텍스트 줄 바꿈과 찾기(Enter/Shift+Enter, 끝에서 처음으로 돌아감). **Excel·한컴 처리기는 실제로 로드됨**(만든 XLSX와 `.hwpx`), 단 **처음 띄울 때 6~10초 걸리고 그동안 창이 멈춥니다**(처리기는 UI 스레드에서 호출해야 함).
- **한글(HWP/HWPX) 전체 쪽 미리보기**: 한컴 셸 처리기(`HncShellExt64.dll`)는 첫 쪽만 그려서, `.hwp`/`.hwpx`는 `HwpText`가 본문 전체를 직접 읽어 내장 텍스트 뷰어로 보여 줍니다(HWPX = zip+XML, HWP = OLE 복합 파일+deflate 레코드, 외부 라이브러리 없음). 글꼴·그림·표 모양은 없고 문단 순서대로의 글이며, 강제 쪽 나누기는 구분선으로 표시합니다. 머리말·꼬리말은 뺍니다. 암호·배포용·DRM 문서는 읽지 못해 기존 처리기로 넘어갑니다. 실제 문서 약 70개로 확인(DRM 문서 5개만 읽지 못함).
- **트리·목록**: 트리 들여쓰기(`TVM_SETINDENT`, 기본/12/24/32), 트리 폴더 이름 색상(트리뷰의 부모 창을 서브클래싱해 `NM_CUSTOMDRAW`, 목록과 같은 색상 규칙의 폴더 규칙), Shift+휠 가로 스크롤(`LVM_SCROLL`), 정렬 열 강조 끄기.
- **실행 취소**(편집 > 실행 취소, Ctrl+Z): 목록의 빈 곳 컨텍스트 메뉴(`IShellView.GetItemObject(SVGIO_BACKGROUND)`)의 `undo` 동사를 실행합니다. 복사 뒤 실행 취소로 복사본이 지워지는 것을 확인했습니다. **그 항목은 되돌릴 게 없을 때도 활성이라서 메뉴를 끌 수 없고, 되돌릴 것이 없으면 아무 일도 일어나지 않습니다.**
- **아직 없는 것**: 3단계의 탭 그룹·탭 너비 모드·기본 탭, 5단계에서 보류한 트리 안의 파일 작업·드래그 동작 점검, 설치 프로그램, Q-Dir 레이아웃 번호 대응표와 열·정렬 스트림 가져오기, 오류 메시지 전체의 영어 번역, 실제 프린터 출력·탐색기 우클릭 메뉴·트레이 아이콘의 실제 표시 확인.

---

## 4. 검증 체크리스트 (Q-Dir와 동작 비교)

- [ ] 4분할에서 각 패널이 독립적으로 탐색하고, 패널 간 드래그앤드롭 복사/이동이 됨
- [ ] 가상 폴더(내 PC, 휴지통, 네트워크, 라이브러리, zip 내부)를 탐색할 수 있음
- [ ] 다시 시작하면 레이아웃·탭·폴더·뷰 모드·열·정렬이 복원됨
- [ ] 색상 필터와 제외 필터가 모든 뷰 모드에서 적용됨
- [ ] 우클릭 메뉴가 탐색기와 같음(셸 확장 포함)
- [ ] 고DPI·다중 모니터·다크 모드에서 UI가 깨지지 않음
- [ ] 1만 개 이상 파일이 있는 폴더에서도 반응이 빠름
- [ ] 포터블 모드에서 레지스트리에 아무것도 쓰지 않음

## 5. 주요 위험 요소와 대응

| 위험 | 대응 |
|---|---|
| .NET에서 `IExplorerBrowser` 호스팅 시 포커스·키보드 문제 | 1단계에서 PoC로 먼저 검증. `IInputObject`, `TranslateAccelerator`를 전달하고, 실패하면 C++/CLI 브리지 또는 C++ 전환을 검토 |
| 셸 내부 ListView 색상 변경(비공개 구조 의존) | ✅ 해소: `FVO_VISTALAYOUT`으로 `SysListView32` 모드가 되는 것을 실측함. Q-Dir도 같은 `SysListView32` + `NM_CUSTOMDRAW` 방식(REA 분석 1.6) |
| 탭이 많을 때 메모리 사용 | 활성 탭만 브라우저 인스턴스를 유지하고 나머지는 지연 생성 |
| 폴더 크기 계산 부하 | 취소 가능한 백그라운드 작업, 결과 캐시, 네트워크 드라이브 제외 옵션 |
| 원본 상표·리소스 | 아이콘·문자열·언어팩·바이너리는 재사용하지 않고 기능만 독자적으로 구현. `.qdr` 가져오기는 형식 호환만 제공 |
