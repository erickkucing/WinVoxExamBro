# Aturan & Panduan QA WinVoxExamBro

Dokumen ini mendefinisikan pagar pengaman, kategori pengujian, dan alur kerja Quality Assurance (QA) untuk repositori **WinVoxExamBro**.

## 1. Aturan Keras Lingkungan Produksi
- **DILARANG mengubah berkas apa pun di `/root/project/projectCBT`** dari proses QA WinVoxExamBro. Repositori `projectCBT` diperlakukan sebagai referensi kontrak *read-only*.
- **DILARANG menjalankan suite PHPUnit atau Playwright milik `projectCBT` terhadap host produksi (`192.168.70.9`, port `80`/`443`/`8008`)** karena terhubung langsung ke basis data dan Redis produksi.
- Semua server tiruan (Fake CBT / Node contract server / `FakeCbtServer` Kestrel) wajib menggunakan port ephemeral `127.0.0.1:0` atau port lokal terisolasi (mis. `127.0.0.1:18089`) dan tidak boleh menyentuh port `80`, `443`, atau `8008`.
- Pengujian integrasi `projectCBT` hanya dijalankan di container sekali pakai yang terisolasi pada GitHub Actions (`mysql:8.0` + `redis:7`, `--env=testing`).

## 2. Kategori Pengujian & Filter xUnit

| Kategori (`[Trait("Category", ...)]`) | Lokasi | Target Eksekusi | Keterangan |
| :--- | :--- | :--- | :--- |
| *(Default / Unit)* | `tests/ExamBro.Tests` | Linux & Windows | Tes unit cepat; dijalankan otomatis oleh `push_to_github.sh` dan job CI `unit-and-stress`. |
| `Stress` | `tests/ExamBro.Tests/Concurrency` | Linux & Windows | Tes konkurensi & *race condition*. Mengambil jumlah iterasi dari `STRESS_ITERATIONS` (default `300`, nightly CI `5000`). |
| `WindowsOnly` | `tests/ExamBro.Tests.Windows` | Windows (`windows-latest` / PC lab) | Tes E2E WPF + WebView2 + FlaUI UIA3 + Win32. |
| Contract Playwright | `tests/e2e-contract` | Linux & Windows (Node.js) | Memverifikasi kontrak JS bridge `src/ExamBro.Exam/Resources/cbt-bridge.js` di Chromium headless. |

## 3. Variabel Lingkungan Pengujian
- `STRESS_ITERATIONS`: Mengatur jumlah iterasi pengulangan pada `[Trait("Category", "Stress")]` (default: `300`).
- `EXAMBRO_E2E=1` & `EXAMBRO_E2E_CDP_PORT`: Hanya aktif pada build **Debug** (`#if DEBUG`) untuk inspeksi DOM WebView2 saat E2E Windows. Build **Release** tidak pernah membuka port debug.
- `EXAMBRO_E2E_ALLOW_REGISTRY=1` (otomatis aktif bila `GITHUB_ACTIONS=true`): Mengizinkan tes E2E yang memodifikasi HKLM/HKCU/Scheduled Task berjalan. Tanpa variabel ini, tes mutasi registry dilewati secara aman agar tidak mengganggu PC developer.

## 4. Baseline Awal
- Baseline awal sebelum eksekusi QA: **120/120 tes unit xUnit lolos** pada `tests/ExamBro.Tests/ExamBro.Tests.csproj` (`-c Release`).
- Coverage diukur dengan `coverlet.collector` untuk laporan (tanpa gate keras).
