# ExamBro - Secure Kiosk Exam Browser for Windows 10/11

ExamBro adalah solusi peramban ujian aman (*secure exam browser*) berbasis native Windows Presentation Foundation (WPF) dan Microsoft Edge WebView2, yang dirancang khusus untuk mengunci sistem operasi Windows 10 dan Windows 11 selama pelaksanaan ujian berbasis komputer (CBT / Computer-Based Testing).

---

## DAFTAR ISI
1. [Fitur Utama](#fitur-utama)
2. [Arsitektur Solusi](#arsitektur-solusi)
3. [Arsitektur Keamanan & Hardening](#arsitektur-keamanan--hardening)
4. [Mode Pengembangan Aman (--dev-safe)](#mode-pengembangan-aman---dev-safe)
5. [Mekanisme Pemulihan Sistem (Crash Recovery)](#mekanisme-pemulihan-sistem-crash-recovery)
6. [Prasyarat Sistem](#prasyarat-sistem)
7. [Panduan Build & Publish](#panduan-build--publish)
8. [Panduan Deployment & Konfigurasi Awal](#panduan-deployment--konfigurasi-awal)
9. [Daftar Asumsi & Keputusan Keamanan](#daftar-asumsi--keputusan-keamanan)
10. [Checklist Pengujian Manual untuk Pengawas / QA](#checklist-pengujian-manual-untuk-pengawas--qa)

---

## FITUR UTAMA

- **Single Instance Enforcement**: Menggunakan named system Mutex (`Global\ExamBro_SingleInstance_Mutex`).
- **Kiosk Fullscreen & Topmost Lock**: Window tanpa border, menutupi seluruh layar, selalu berada di lapisan paling atas (*Topmost*), taskbar Windows disembunyikan.
- **Focus Guard Aktif**: Memasang `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` dan `AttachThreadInput` untuk segera merebut kembali fokus jika ada jendela asing yang mencoba muncul.
- **Pemblokiran Keyboard Tingkat Rendah**: Low-Level Keyboard Hook (`WH_KEYBOARD_LL`) di-pin di memori untuk memblokir tombol Windows (kiri/kanan), Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4, Alt+Space, Ctrl+Shift+Esc, PrintScreen, Menu/Apps, tombol fungsi (F1, F11, F12), dan shortcut browser (Ctrl+N, Ctrl+T, Ctrl+W, Ctrl+Shift+I/J/C, dll.).
- **Pembatasan Sistem Tanpa Admin**: Menonaktifkan Task Manager (`DisableTaskMgr`), Lock Workstation (`DisableLockWorkstation`), Change Password (`DisableChangePassword`), dan Logoff (`NoLogoff`) via HKCU registry policies. Menonaktifkan shortcut aksesibilitas (StickyKeys, FilterKeys, ToggleKeys).
- **Anti-Screenshot & Anti-Recording**: Proteksi layar menggunakan API `SetWindowDisplayAffinity` (`WDA_EXCLUDEFROMCAPTURE`, fallback `WDA_MONITOR`).
- **Hardening WebView2**: Profil InPrivate terisolasi di `%LocalAppData%\ExamBro\WebView2Profile`, DevTools nonaktif, klik kanan dinonaktifkan, accelerator keys browser dimatikan, download dibatalkan, pembatasan izin perangkat keras (kamera, mikrofon, lokasi), dan error page internal kustom.
- **Whitelist Navigasi Ketat**: Hanya mengizinkan URL yang cocok dengan pola wildcard yang diizinkan. Pemblokiran skema berbahaya (`file:`, `javascript:`, `view-source:`, dll.). Deteksi Exit URL otomatis untuk menyelesaikan ujian.
- **Monitoring Lingkungan**: Pemantauan proses terlarang per 1 detik (dengan pengecualian proses internal WebView2), deteksi Virtual Machine (WMI BIOS, MAC OUI, registry, hypervisor CPUID), deteksi Remote Desktop (`SM_REMOTESESSION`), deteksi debugger, dan pemantauan jumlah monitor.
- **Penyimpanan Terenkripsi AES-256-GCM**: Konfigurasi `%ProgramData%\ExamBro\exambro.cfg` dienkripsi dengan AES-256-GCM menggunakan master key yang dilindungi DPAPI (LocalMachine) + entropy khusus. Setiap perubahan manual pada file akan terdeteksi sebagai modifikasi (*tampering*) dan ditolak.
- **Keluar Aman & Anti Brute-Force**: Akses keluar hanya via shortcut pengawas (default `Ctrl+Shift+Q`) atau tombol toolbar dengan password pengawas yang diverifikasi menggunakan PBKDF2-HMAC-SHA256 (310.000 iterasi, constant-time compare). Lockout otomatis 30 detik setelah 5 kali salah.

---

## ARSITEKTUR SOLUSI

Proyek ini dibangun menggunakan struktur modular multi-project:

```
WinVoxExamBro/
├── Directory.Build.props                  # Pengaturan build global (.NET 8, C# latest, Nullable)
├── ExamBro.sln                            # Visual Studio Solution File
├── src/
│   ├── ExamBro.Core/                      # Class Library (.NET 8)
│   │   ├── Constants.cs                   # Konstanta global, path, crypto salt, GUID
│   │   ├── Models/
│   │   │   └── ExamConfig.cs              # Struktur model konfigurasi & enums
│   │   ├── Security/
│   │   │   ├── ConfigEncryption.cs        # Enkripsi AES-256-GCM, DPAPI, ekspor/impor .exbcfg
│   │   │   └── PasswordHasher.cs          # PBKDF2-HMAC-SHA256 & constant-time verify
│   │   ├── Validation/
│   │   │   ├── UrlMatcher.cs              # URL matching, glob to regex, scheme blocking
│   │   │   └── ConfigValidator.cs         # Validasi integritas & kelengkapan konfigurasi
│   │   ├── Logging/
│   │   │   ├── LoggerSetup.cs             # Inisialisasi Serilog rolling daily file sink
│   │   │   └── WebhookSink.cs             # Sink HTTPS webhook dengan verifikasi HMAC
│   │   └── Recovery/
│   │       └── RecoveryState.cs           # State persistensi pemulihan sistem pasca crash
│   │
│   ├── ExamBro.Exam/                      # WPF Application (Kiosk Client)
│   │   ├── App.xaml / App.xaml.cs         # Startup, pre-flight check, single instance, recovery
│   │   ├── Services/
│   │   │   ├── KioskLockService.cs        # Win32 taskbar hide, HKCU registry, accessibility keys
│   │   │   ├── KeyboardHookService.cs     # WH_KEYBOARD_LL low-level keyboard hook
│   │   │   ├── FocusGuardService.cs       # SetWinEventHook foreground focus keeper
│   │   │   ├── ClipboardService.cs        # Pembersihan & pemantauan clipboard format listener
│   │   │   ├── ProcessMonitorService.cs   # Scanner proses background berkala 1 detik
│   │   │   ├── EnvironmentDetector.cs     # Deteksi VM, RDP, Debugger, monitor change
│   │   │   ├── ScreenProtectionService.cs # SetWindowDisplayAffinity & secondary monitor overlay
│   │   │   └── ViolationManager.cs        # Manajemen & eskalasi pelanggaran peserta
│   │   ├── ViewModels/
│   │   │   ├── ExamViewModel.cs           # State toolbar, jam, baterai, jaringan, navigasi
│   │   │   └── PreFlightViewModel.cs      # State pengujian pra-terbang
│   │   ├── Views/
│   │   │   ├── ExamWindow.xaml (.cs)      # Jendela utama browser kiosk & hardening WebView2
│   │   │   ├── PreFlightDialog.xaml (.cs) # Dialog hasil diagnosa sebelum ujian dimulai
│   │   │   ├── WarningOverlay.xaml (.cs)  # Overlay peringatan pelanggaran (auto-dismiss)
│   │   │   ├── LockScreen.xaml (.cs)      # Layar kunci pelanggaran dengan password pengawas
│   │   │   ├── ExitDialog.xaml (.cs)      # Dialog konfirmasi keluar pengawas
│   │   │   ├── SecondaryScreenOverlayWindow.xaml (.cs) # Penutup layar monitor sekunder
│   │   │   └── CompletionOverlay.xaml (.cs)            # Notifikasi otomatis ujian selesai
│   │   └── Resources/
│   │       ├── ErrorPage.html             # Halaman error offline kustom
│   │       └── exam.ico                   # Ikon aplikasi
│   │
│   └── ExamBro.Config/                    # WPF Application (Admin Configuration Tool)
│       ├── app.manifest                   # requireAdministrator UAC elevation
│       ├── App.xaml / App.xaml.cs         # Inisialisasi, first-run wizard check, login prompt
│       ├── ViewModels/
│       │   ├── MainViewModel.cs           # Logika manajemen form tab, save, export/import
│       │   └── ViolationRuleItemViewModel.cs
│       ├── Views/
│       │   ├── MainWindow.xaml (.cs)      # UI Pengaturan 6 Tab (Umum, Navigasi, Keamanan, dll)
│       │   ├── LoginDialog.xaml (.cs)     # Autentikasi Admin dengan pelindung brute-force
│       │   ├── SetupWizardDialog.xaml (.cs) # Wisaya inisialisasi awal saat pertama kali dijalankan
│       │   ├── TestUrlWindow.xaml (.cs)   # Jendela uji coba jangkauan Start URL non-kiosk
│       │   ├── ChangePasswordDialog.xaml (.cs) # Dialog pergantian password Admin & Pengawas
│       │   └── PasswordPromptDialog.xaml (.cs) # Dialog input password untuk .exbcfg
│       └── Converters/
│           ├── BoolToVisibilityConverter.cs
│           └── EnumToStringConverter.cs
│
├── tests/
│   └── ExamBro.Tests/                     # xUnit Test Suite (.NET 8)
│       ├── UrlMatcherTests.cs             # Pengujian wildcard, blacklist, blocked schemes
│       ├── PasswordHasherTests.cs         # Pengujian hashing PBKDF2 & constant-time verify
│       ├── ConfigEncryptionTests.cs      # Pengujian export/import AES-GCM & deteksi manipulasi
│       └── ConfigValidatorTests.cs        # Pengujian aturan validasi konfigurasi
│
└── installer/
    └── setup.iss                          # Script Inno Setup Compiler (x64, ACL, WebView2 bootstrap)
```

---

## ARSITEKTUR KEAMANAN & HARDENING

### 1. Kiosk Lock & P/Invoke Win32
- **Penyembunyian Taskbar**:
  Menggunakan `FindWindow("Shell_TrayWnd", null)` dan `FindWindow("Shell_SecondaryTrayWnd", null)`, kemudian dipanggil `ShowWindow(hWnd, SW_HIDE)`.
- **Keyboard Hooking**:
  `SetWindowsHookEx(WH_KEYBOARD_LL, ...)` dengan delegate yang disimpan di field class yang di-pin agar tidak terkena Garbage Collection. Semua kombinasi tombol sistem diblokir pada tingkat kernel driver message queue sebelum sampai ke aplikasi mana pun.
- **Focus Guard**:
  `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` mendengarkan perpindahan jendela aktif. Jika fokus keluar dari ExamBro, aplikasi langsung mengembalikan fokus menggunakan `AttachThreadInput` dan `SetForegroundWindow`.
- **Proteksi Layar**:
  `SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE)` memastikan bahwa perekam layar (seperti OBS, Discord, Windows Game Bar) dan screenshot tool (Snipping Tool, PrintScreen) hanya menangkap warna hitam atau jendela tembus pandang.

### 2. Registry Policies (HKCU)
Diterapkan pada level user saat ujian berlangsung tanpa memerlukan izin Administrator:
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System`
  - `DisableTaskMgr = 1`: Mencegah Task Manager dibuka via Ctrl+Alt+Del atau shortcut apa pun.
  - `DisableLockWorkstation = 1`: Mencegah peserta mengunci komputer.
  - `DisableChangePassword = 1`: Mencegah penggantian kata sandi.
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer`
  - `NoLogoff = 1`: Mencegah logout dari menu Windows.

Nilai awal dari setiap registry key dicatat sebelum diubah, dan dipulihkan sepenuhnya saat sesi ujian ditutup normal, crash, maupun saat shutdown/logoff sistem.

### 3. Hardening WebView2
- **Data Folder Terisolasi**: `%LocalAppData%\ExamBro\WebView2Profile` dibersihkan cache dan cookienya pada saat start dan exit.
- **Konfigurasi Lingkungan Browser**:
  - `AreDevToolsEnabled = false` (Mencegah inspect element).
  - `AreDefaultContextMenusEnabled = false` (Mencegah klik kanan).
  - `AreBrowserAcceleratorKeysEnabled = false` (Mencegah F5, Ctrl+R, Ctrl+P, dll.).
  - `IsStatusBarEnabled = false`.
  - `IsPasswordAutosaveEnabled = false` & `IsGeneralAutofillEnabled = false`.
  - `IsSwipeNavigationEnabled = false` & `IsPinchZoomEnabled = false`.
- **Kontrol Jaringan & Navigasi**:
  - `NavigationStarting`: Memeriksa setiap target URL terhadap `AllowedUrlPatterns` dan `BlockedUrlPatterns`. Jika melanggar, navigasi dibatalkan (`e.Cancel = true`) dan pelanggaran dicatat.
  - `NewWindowRequested`: Selalu diatur `e.Handled = true`. Jika URL tujuan diizinkan, browser diarahkan di tab/jendela yang sama; jika tidak, dibatalkan.
  - `DownloadStarting`: Selalu dibatalkan (`e.Cancel = true`) kecuali opsi `AllowDownloads` diaktifkan di konfigurasi.
  - `PermissionRequested`: Semua izin perangkat keras (kamera, mikrofon, geolokasi, notifikasi) ditolak secara otomatis kecuali yang secara spesifik diizinkan untuk keperluan proctoring online.
  - `User-Agent & Headers`: Ditambahkan suffix ` ExamBro/1.0.0` serta header kustom `X-ExamBro-Version` pada setiap HTTP request.

---

## MODE PENGEMBANGAN AMAN (--dev-safe)

Untuk memfasilitasi pengujian dan pengembangan tanpa mengunci workstation developer:
- Gunakan argumen: `ExamBro.Exam.exe --dev-safe` atau environment variable `EXAMBRO_DEV=1`.
- Dalam mode ini:
  - Keyboard hook **dinonaktifkan**.
  - Taskbar Windows **tidak disembunyikan**.
  - Registry policies (DisableTaskMgr, dll.) **tidak diubah**.
  - Jendela dapat dipindahkan atau ditutup secara normal.
  - Seluruh logika navigasi, whitelist/blacklist, WebView2 hardening, dan verifikasi konfigurasi **tetap berjalan aktif**.
- **Perlindungan Release Build**:
  Mode `--dev-safe` ditolak secara otomatis pada build rilis (`Release`) kecuali jika flag `DevSafeAllowedInRelease=true` diatur secara eksplisit oleh admin dalam konfigurasi.

---

## MEKANISME PEMULIHAN SISTEM (CRASH RECOVERY)

Jika komputer tiba-tiba mati listrik, proses ExamBro di-kill secara paksa oleh sistem operasi, atau terjadi kegagalan tak terduga:
1. Setiap kali Kiosk Lock aktif, file status cadangan ditulis ke `%LocalAppData%\ExamBro\recovery.json`. File ini mencatat seluruh nilai registri asli, status taskbar, dan flag aksesibilitas.
2. Saat aplikasi ExamBro dijalankan berikutnya, sistem mendeteksi keberadaan `recovery.json` dan secara otomatis memulihkan taskbar serta registry kebijakan sebelum melanjutkan.
3. Administrator atau pengawas dapat memulihkan sistem secara manual kapan saja dengan menjalankan:
   ```cmd
   ExamBro.Exam.exe --restore
   ```
   Perintah ini akan membaca file status, mengembalikan registri dan taskbar ke kondisi normal, lalu langsung keluar.

---

## PRASYARAT SISTEM

- **Sistem Operasi**: Windows 10 (versi 1809 ke atas) atau Windows 11 (64-bit).
- **Runtime**:
  - Microsoft Edge WebView2 Evergreen Runtime (otomatis dibundel/diperiksa oleh installer).
  - .NET Desktop Runtime 8.0 (jika menggunakan publish framework-dependent) atau **tidak memerlukan runtime tambahan** jika menggunakan publish self-contained single-file.

---

## PANDUAN BUILD & PUBLISH

### 1. Build & Menjalankan Unit Test
Jalankan perintah berikut di PowerShell atau Command Prompt pada lingkungan pengembangan Windows dengan .NET 8 SDK:

```powershell
# Restore dependensi NuGet
dotnet restore

# Build seluruh solusi dalam konfigurasi Release
dotnet build ExamBro.sln -c Release --no-restore

# Jalankan seluruh unit test
dotnet test tests/ExamBro.Tests/ExamBro.Tests.csproj -c Release
```

### 2. Publish Self-Contained Single-File (win-x64)
Gunakan perintah berikut untuk menghasilkan binary mandiri (*single-file self-contained executable*) tanpa ketergantungan runtime eksternal:

```powershell
# 1. Publish ExamBro Client (Exam Browser)
dotnet publish src/ExamBro.Exam/ExamBro.Exam.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o dist/ExamBro.Exam

# 2. Publish ExamBro Config Tool (Admin Configuration)
dotnet publish src/ExamBro.Config/ExamBro.Config.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o dist/ExamBro.Config
```

Hasil publish akan berada di direktori `dist/ExamBro.Exam/ExamBro.Exam.exe` dan `dist/ExamBro.Config/ExamBro.Config.exe`.

### 3. Kompilasi Installer Inno Setup
Unduh dan pasang [Inno Setup 6](https://jrsoftware.org/isdl.php), kemudian jalankan:

```cmd
iscc installer/setup.iss
```
File installer akhir akan berada di folder `dist/installer/ExamBro-Setup-v1.0.0.exe`.

---

## PANDUAN DEPLOYMENT & KONFIGURASI AWAL

### Langkah 1: Instalasi pada Komputer Lab / Klien
Jalankan `ExamBro-Setup-v1.0.0.exe` dengan hak Administrator. Installer akan:
1. Memasang kedua file aplikasi ke folder Program Files (`C:\Program Files\ExamBro`).
2. Membuat folder konfigurasi di `C:\ProgramData\ExamBro` dan mengatur hak akses (ACL):
   - **Users (Siswa)**: Read & Execute.
   - **Administrators**: Full Control.
3. Memeriksa keberadaan WebView2 Runtime dan memasangnya secara otomatis jika belum ada.
4. Membuat shortcut ExamBro di Desktop dan Start Menu.

### Langkah 2: Konfigurasi Pertama Kali (First-Run Wizard)
1. Buka aplikasi **Pengaturan ExamBro** dari Start Menu (akan meminta konfirmasi UAC Administrator).
2. Wisaya Pengaturan Awal (*Setup Wizard*) akan muncul secara otomatis:
   - Tentukan **Password Administrator** (untuk membuka aplikasi konfigurasi).
   - Tentukan **Password Pengawas / Proctor** (untuk membuka layar terkunci dan keluar dari browser ujian).
   - Masukkan **Start URL** (misal: `https://ujian.sekolah.sch.id/login`).
3. Simpan konfigurasi. File terenkripsi `exambro.cfg` akan dibuat di `C:\ProgramData\ExamBro\exambro.cfg`.

### Langkah 3: Distribusi Konfigurasi ke Seluruh Komputer Klien (Mass Deployment)
1. Di komputer utama, buka `ExamBro.Config.exe` -> Tab **Admin**.
2. Klik tombol **Ekspor Konfigurasi (.exbcfg)** dan masukkan password enkripsi paket.
3. Salin file `.exbcfg` tersebut ke komputer klien (bisa via flashdisk atau jaringan lokal).
4. Pada komputer klien, jalankan `ExamBro.Config.exe` -> Tab **Admin** -> Klik **Impor Konfigurasi (.exbcfg)**, masukkan password paket, lalu simpan.

---

## DAFTAR ASUMSI & KEPUTUSAN KEAMANAN

| Area | Keputusan / Asumsi yang Diambil | Alasan Keamanan |
| :--- | :--- | :--- |
| **Prioritas Keamanan** | Keamanan > Stabilitas > Fitur Tambahan | Jika ada ambiguitas perilaku, sistem memilih opsi paling ketat untuk mencegah kecurangan. |
| **Penyimpanan Konfigurasi** | AES-256-GCM + DPAPI (LocalMachine) + AppEntropy | Memastikan file konfigurasi tidak dapat dibaca atau dimodifikasi oleh peserta ujian bahkan jika mereka memiliki akses baca lokal. Tag autentikasi GCM mendeteksi modifikasi 1-bit sekalipun. |
| **Hak Akses Klien Ujian** | Client `ExamBro.Exam.exe` berjalan dengan hak User standar (non-admin) | Siswa di lab komputer umumnya login sebagai user non-admin. Semua pembatasan kiosk (registry HKCU, hooks) dirancang agar berfungsi optimal tanpa memerlukan hak admin. |
| **Ctrl+Alt+Del** | Tidak dapat diblokir via keyboard hook Win32; dicegah melalui HKCU Registry Policies | Arsitektur keamanan kernel Windows melindungi urutan SAS (Secure Attention Sequence). Oleh karena itu, Task Manager, Change Password, Lock, dan Logoff dinonaktifkan di registry selama sesi ujian. |
| **Start URL Override** | Argumen command line `--url=` diabaikan secara default | Mencegah peserta meluncurkan ujian ke server phising/palsu melalui manipulasi shortcut. Hanya diizinkan jika `AllowCommandLineOverride=true` diatur oleh admin. |
| **Blacklist vs Whitelist URL** | Blacklist selalu menang atas Whitelist | Jika suatu pola URL cocok dengan whitelist tetapi juga cocok dengan blacklist, URL tersebut tetap diblokir. |
| **Skema Protokol Berbahaya** | Skema `file:`, `javascript:`, `view-source:`, `edge:`, `chrome:`, `data:`, `ftp:` diblokir mutlak | Mencegah akses ke file sistem lokal, eksekusi skrip injeksi, atau membuka halaman konfigurasi peramban internal. |
| **Penanganan Proses Milik WebView2** | Proses dengan nama `msedgewebview2.exe` yang merupakan child dari proses ExamBro dikecualikan dari blacklist | WebView2 memerlukan proses rendering dan utility helper miliknya sendiri untuk menampilkan halaman web. Hanya proses browser independen (Edge, Chrome, Firefox) yang ditindak. |
| **Brute Force Lockout** | 5 kali kesalahan password -> penguncian 30 detik | Melindungi dialog keluar dan layar kunci dari upaya tebakan password berulang oleh peserta. |
| **Recovery Pasca Crash** | Deteksi otomatis file `%LocalAppData%\ExamBro\recovery.json` pada peluncuran berikutnya | Menjamin workstation lab kembali ke kondisi normal meskipun terjadi insiden listrik padam atau crash fatal. |

---

## CHECKLIST PENGUJIAN MANUAL UNTUK PENGAWAS / QA

Gunakan daftar centang berikut untuk memvalidasi kesiapan sistem sebelum ujian berlangsung:

### 1. Uji Coba Kiosk & Isolasi Desktop
- [ ] **Layar Penuh**: ExamBro terbuka secara fullscreen tanpa title bar, border, tombol minimize, maximize, atau close.
- [ ] **Taskbar**: Taskbar Windows menghilang sepenuhnya dari monitor utama dan monitor sekunder.
- [ ] **Kunci Fokus**: Klik mouse di luar jendela (jika terlihat sedikit) atau mencoba menekan tombol kombinasi tidak memindahkan fokus dari ExamBro.

### 2. Uji Coba Pemblokiran Tombol Keyboard
- [ ] Tekan tombol **Windows (kiri / kanan)**: Menu Start tidak terbuka.
- [ ] Tekan **Alt + Tab**: Jendela switcher aplikasi tidak muncul.
- [ ] Tekan **Alt + F4**: Aplikasi tidak tertutup.
- [ ] Tekan **Ctrl + Esc**: Menu Start tidak terbuka.
- [ ] Tekan **Ctrl + Shift + Esc**: Task Manager tidak terbuka.
- [ ] Tekan **PrintScreen**: Tidak ada aksi tangkapan layar, clipboard tidak terisi gambar.
- [ ] Tekan **F12**, **Ctrl + Shift + I**, **Ctrl + Shift + J**: Konsol Developer Tools WebView2 tidak muncul.
- [ ] Tekan **Ctrl + N**, **Ctrl + T**, **Ctrl + W**: Tidak membuka tab atau jendela peramban baru.

### 3. Uji Coba Keamanan Sistem & Registry
- [ ] Tekan **Ctrl + Alt + Del**: Opsi *Task Manager*, *Lock*, dan *Change a password* berstatus abu-abu (disabled) atau tidak berfungsi.
- [ ] Jalankan perintah `taskmgr` dari shortcut (jika ada): Muncul pesan bahwa Task Manager telah dinonaktifkan oleh Administrator.

### 4. Uji Coba Kontrol Navigasi Browser
- [ ] **Start URL Otomatis**: Aplikasi langsung membuka halaman Start URL yang dikonfigurasi tanpa halaman awal perantara.
- [ ] **Klik Kanan**: Menu konteks browser tidak muncul saat klik kanan pada halaman ujian.
- [ ] **Tautan Eksternal**: Mengklik tautan menuju domain di luar whitelist diblokir dan memunculkan peringatan atau tetap berada di halaman ujian.
- [ ] **Protokol Terlarang**: Menavigasi ke `file:///C:/` atau `javascript:...` diblokir secara otomatis.
- [ ] **Exit URL**: Ketika peserta menyelesaikan ujian dan diarahkan ke URL akhir (Exit URL), muncul overlay "Ujian Selesai" dan aplikasi menutup otomatis setelah hitungan mundur.

### 5. Uji Coba Multi-Monitor & Proteksi Tangkapan Layar
- [ ] Pasang monitor kedua (kabel HDMI/DisplayPort): Monitor kedua tertutup layar hitam pekat (*CoverAll*) atau aplikasi menolak berjalan (*Block* sesuai konfigurasi).
- [ ] Buka aplikasi OBS Studio atau Discord di latar belakang: Rekaman layar pada jendela ExamBro hanya menampilkan layar hitam pekat (*Display Affinity Exclusion*).

### 6. Uji Coba Keluar & Pemulihan Sistem
- [ ] Tekan shortcut rahasia **Ctrl + Shift + Q**: Dialog autentikasi pengawas muncul.
- [ ] Masukkan password salah sebanyak 5 kali: Sistem terkunci selama 30 detik.
- [ ] Masukkan password pengawas yang benar: Aplikasi menutup secara elegan.
- [ ] **Verifikasi Pemulihan**: Taskbar Windows kembali terlihat, Task Manager dapat dibuka kembali secara normal, dan fungsi tombol Windows kembali aktif.
- [ ] **Simulasi Crash**: Jalankan ExamBro, lalu kill proses `ExamBro.Exam.exe` via remote tool / command prompt. Jalankan `ExamBro.Exam.exe --restore`. Pastikan taskbar dan registri kembali normal 100%.
