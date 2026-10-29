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
| 0단계 | ✅ 완료 (2026-10-28) | `MDir.slnx`, `src/MDir`(WinForms, .NET 10, 실행 파일 `M-Dir.exe`), `tests/MDir.Tests`(xUnit) |
| 1단계 | ✅ 완료 (2026-10-28) | `ExplorerBrowserHost`(IExplorerBrowser 호스팅), 자체 탐색 기록, 패널 단위 보기 모드, 키보드 라우팅(화살표·Tab·Backspace·셸 단축키), 마우스 뒤로/앞으로 버튼, 주소창 자동 완성. 테스트 14개 통과(통합 테스트 3개는 실제 셸 뷰로 검증) |
| 4단계 | ⚠️ 일부 완료 (2026-10-29) | 완료: `IniFile`(UTF-16, 원자적 저장), `AppPaths`(포터블 `M-Dir.ini` 감지 / `%APPDATA%\M-Dir`), `AppSettings`, `Workspace`(`.mdr`: 레이아웃·분할 비율·패널별 탭 PIDL·보기 모드·활성 패널·창 위치), `start.mdr` 자동 저장/복원, 파일 메뉴(새로 만들기 Ctrl+N / 열기 Ctrl+O / 저장 Ctrl+S / 다른 이름으로), 명령줄 `.mdr`·폴더 인자. **미완료: 패널별 열·정렬 저장, Q-Dir `.qdr` 가져오기, 트리·미리보기 상태(5·8단계에서 추가).** 테스트 96개 통과 |
| 3단계 | ✅ 완료 (2026-10-29) | `PaneTab`(탭별 기록·보기 모드), `PaneTabStrip`(직접 그린 탭 막대: 닫기 버튼, 가운데 클릭·더블클릭 닫기, 드래그 순서 변경, "+" 버튼, 여러 줄 모드, 넘칠 때 목록), 주소창 드롭다운(자주 쓰는 폴더 + 드라이브, Ctrl+클릭 = 새 탭), Alt+Enter = 새 탭에서 열기, 탭 메뉴(Ctrl+T/W, Ctrl+Tab, Ctrl+Alt+A, 복제). 테스트 53개 통과 |
| 2단계 | ✅ 완료 (2026-10-29) | `PaneControl`(탐색 막대 + 셸 뷰 + 상태 표시줄, 활성 패널 테두리), `PaneLayout`(레이아웃 13종 분할 트리), `PaneArranger`(배치 계산), `PaneLayoutPanel`(분할선 드래그·더블클릭 초기화), 레이아웃 메뉴(Alt+L, 아이콘), F6/Shift+F6/Ctrl+Alt+1~4. 테스트 43개 통과 |

1단계에서 결정한 사항:
- 탐색 기록은 `EBO_NOTRAVELLOG`로 브라우저 자체 기록을 끄고 `NavigationHistory<ShellLocation>`로 직접 관리합니다(3단계 탭 전환 시 브라우저를 다시 만들어도 기록이 남도록).
- 보기 모드는 `EBO_NOPERSISTVIEWSTATE`로 폴더별 저장을 끄고 패널 단위로 유지합니다(Q-Dir의 `ViewModeN`과 같은 방식).
- 선택 변경 이벤트는 임시로 250ms 간격 폴링을 씁니다. 6단계에서 `ICommDlgBrowser`(Q-Dir도 구현함)를 넣으면서 `OnStateChange`로 바꿉니다.

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
- **보류한 것과 이유**:
  - 열·정렬 저장: `EBO_NOPERSISTVIEWSTATE` 때문에 셸이 폴더마다 정렬을 기억하지 않으므로, Q-Dir처럼 **폴더 종류(CLSID)별 보기 상태 저장소**(`Spatlen_` 키, `IFolderView2.GetSortColumns`/`IColumnManager`)를 따로 설계해야 합니다. 탭 단위로 저장하면 폴더를 옮길 때 엉뚱한 정렬이 적용됩니다.
  - `.qdr` 가져오기: Q-Dir `ViewMode` 값과 M-Dir 레이아웃 번호의 대응표를 아직 확인하지 못했습니다(`ViewMode=0`이 4분할 기본값인 것만 확인).

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
