#!/usr/bin/env python3
"""
WinVoxExamBro Documentation Generator
Generates:
1. docs/Panduan_Lengkap_WinVoxExamBro.docx (Microsoft Word)
2. docs/Panduan_Lengkap_WinVoxExamBro.pdf (PDF Document)

Author & Copyright: Erick Kresnamurti
"""

import os
import sys
from datetime import datetime

# ==============================================================================
# DOCX GENERATION
# ==============================================================================
import docx
from docx.shared import Inches, Pt, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.enum.table import WD_TABLE_ALIGNMENT, WD_ALIGN_VERTICAL
from docx.oxml import OxmlElement, parse_xml
from docx.oxml.ns import nsdecls, qn

# ==============================================================================
# PDF GENERATION (ReportLab Platypus)
# ==============================================================================
from reportlab.lib.pagesizes import A4
from reportlab.platypus import (
    SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle, PageBreak, KeepTogether, HRFlowable
)
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib import colors
from reportlab.pdfgen import canvas

# Palet Warna Dokumen
COLOR_PRIMARY = RGBColor(15, 23, 42)     # Slate 900
COLOR_SECONDARY = RGBColor(30, 58, 138)  # Blue 900
COLOR_ACCENT = RGBColor(37, 99, 235)    # Blue 600
COLOR_MUTED = RGBColor(71, 85, 105)      # Slate 600
COLOR_BORDER = RGBColor(203, 213, 225)   # Slate 300

RL_PRIMARY = colors.HexColor("#0f172a")
RL_SECONDARY = colors.HexColor("#1e3a8a")
RL_ACCENT = colors.HexColor("#2563eb")
RL_MUTED = colors.HexColor("#475569")
RL_BG_LIGHT = colors.HexColor("#f8fafc")
RL_BORDER = colors.HexColor("#cbd5e1")
RL_HEADER_BG = colors.HexColor("#1e293b")


def set_cell_margins(cell, top=100, bottom=100, left=150, right=150):
    """Atur margin padding sel tabel docx."""
    tc = cell._tc
    tcPr = tc.get_or_add_tcPr()
    tcMar = OxmlElement('w:tcMar')
    for m, val in [('top', top), ('bottom', bottom), ('left', left), ('right', right)]:
        node = OxmlElement(f'w:{m}')
        node.set(qn('w:w'), str(val))
        node.set(qn('w:type'), 'dxa')
        tcMar.append(node)
    tcPr.append(tcMar)


def set_cell_background(cell, hex_color):
    """Atur warna latar belakang sel tabel docx."""
    shading_elm = parse_xml(f'<w:shd {nsdecls("w")} w:fill="{hex_color}"/>')
    cell._tc.get_or_add_tcPr().append(shading_elm)


def build_docx(filepath):
    print(f"Membuat dokumen Word: {filepath}...")
    doc = docx.Document()

    # Metadata Dokumen
    doc.core_properties.title = "WinVox ExamBro - Panduan Lengkap & Deployment"
    doc.core_properties.author = "Erick Kresnamurti"
    doc.core_properties.comments = "Hak Cipta © 2024-2026 Erick Kresnamurti. Seluruh hak cipta dilindungi."

    # Margin Halaman (Normal 1 inch)
    sections = doc.sections
    for section in sections:
        section.top_margin = Inches(1)
        section.bottom_margin = Inches(1)
        section.left_margin = Inches(1)
        section.right_margin = Inches(1)

        # Header & Footer
        header = section.header
        hp = header.paragraphs[0]
        hp.alignment = WD_ALIGN_PARAGRAPH.RIGHT
        hrun = hp.add_run("WinVox ExamBro - Panduan Resmi & Dokumentasi Teknis")
        hrun.font.name = "Segoe UI"
        hrun.font.size = Pt(8.5)
        hrun.font.color.rgb = COLOR_MUTED

        footer = section.footer
        fp = footer.paragraphs[0]
        fp.alignment = WD_ALIGN_PARAGRAPH.LEFT
        frun1 = fp.add_run("Hak Cipta © 2024-2026 Erick Kresnamurti. Seluruh hak cipta dilindungi.")
        frun1.font.name = "Segoe UI"
        frun1.font.size = Pt(8.5)
        frun1.font.color.rgb = COLOR_MUTED

    # Judul Dokumen (Cover Area)
    title_p = doc.add_paragraph()
    title_p.paragraph_format.space_before = Pt(20)
    title_p.paragraph_format.space_after = Pt(6)
    r_title = title_p.add_run("WinVox ExamBro")
    r_title.font.name = "Segoe UI"
    r_title.font.size = Pt(28)
    r_title.font.bold = True
    r_title.font.color.rgb = COLOR_PRIMARY

    sub_p = doc.add_paragraph()
    sub_p.paragraph_format.space_before = Pt(0)
    sub_p.paragraph_format.space_after = Pt(14)
    r_sub = sub_p.add_run("Panduan Lengkap, Deployment, dan Dokumentasi Teknis Sistem Browser Ujian Kiosk Windows 10 & 11")
    r_sub.font.name = "Segoe UI"
    r_sub.font.size = Pt(13)
    r_sub.font.color.rgb = COLOR_SECONDARY

    # Metadata Box
    meta_table = doc.add_table(rows=4, cols=2)
    meta_table.alignment = WD_TABLE_ALIGNMENT.CENTER
    meta_table.autofit = False

    meta_data = [
        ("Pengembang & Hak Cipta", "Erick Kresnamurti"),
        ("Versi Aplikasi", "1.0.0 (Release Native x64)"),
        ("Kerangka Kerja", ".NET 8.0 WPF, Microsoft Edge WebView2 Evergreen"),
        ("Pembaruan Terakhir", "Tahun 2024 - 2026")
    ]

    for i, (k, v) in enumerate(meta_data):
        row = meta_table.rows[i]
        c0 = row.cells[0]
        c1 = row.cells[1]
        c0.width = Inches(2.2)
        c1.width = Inches(4.3)
        set_cell_margins(c0, 60, 60, 100, 100)
        set_cell_margins(c1, 60, 60, 100, 100)
        set_cell_background(c0, "F1F5F9")
        set_cell_background(c1, "F8FAFC")

        p0 = c0.paragraphs[0]
        p0.paragraph_format.space_after = Pt(0)
        r0 = p0.add_run(k)
        r0.font.name = "Segoe UI"
        r0.font.bold = True
        r0.font.size = Pt(9.5)
        r0.font.color.rgb = COLOR_PRIMARY

        p1 = c1.paragraphs[0]
        p1.paragraph_format.space_after = Pt(0)
        r1 = p1.add_run(v)
        r1.font.name = "Segoe UI"
        r1.font.size = Pt(9.5)
        r1.font.color.rgb = COLOR_PRIMARY

    doc.add_paragraph().paragraph_format.space_after = Pt(16)

    def add_h1(text):
        p = doc.add_paragraph()
        p.paragraph_format.space_before = Pt(18)
        p.paragraph_format.space_after = Pt(6)
        p.paragraph_format.keep_with_next = True
        r = p.add_run(text)
        r.font.name = "Segoe UI"
        r.font.size = Pt(16)
        r.font.bold = True
        r.font.color.rgb = COLOR_SECONDARY
        return p

    def add_h2(text):
        p = doc.add_paragraph()
        p.paragraph_format.space_before = Pt(12)
        p.paragraph_format.space_after = Pt(4)
        p.paragraph_format.keep_with_next = True
        r = p.add_run(text)
        r.font.name = "Segoe UI"
        r.font.size = Pt(12.5)
        r.font.bold = True
        r.font.color.rgb = COLOR_PRIMARY
        return p

    def add_p(text, bold=False, italic=False):
        p = doc.add_paragraph()
        p.paragraph_format.space_before = Pt(0)
        p.paragraph_format.space_after = Pt(5)
        r = p.add_run(text)
        r.font.name = "Segoe UI"
        r.font.size = Pt(10)
        r.font.bold = bold
        r.font.italic = italic
        r.font.color.rgb = COLOR_PRIMARY
        return p

    def add_bullet(text, bold_prefix=None):
        p = doc.add_paragraph(style='List Bullet')
        p.paragraph_format.space_before = Pt(0)
        p.paragraph_format.space_after = Pt(3)
        if bold_prefix:
            r_pre = p.add_run(bold_prefix)
            r_pre.font.name = "Segoe UI"
            r_pre.font.size = Pt(10)
            r_pre.font.bold = True
            r_pre.font.color.rgb = COLOR_PRIMARY
        r = p.add_run(text)
        r.font.name = "Segoe UI"
        r.font.size = Pt(10)
        r.font.color.rgb = COLOR_PRIMARY
        return p

    def add_code(code_text):
        tbl = doc.add_table(rows=1, cols=1)
        tbl.alignment = WD_TABLE_ALIGNMENT.CENTER
        cell = tbl.rows[0].cells[0]
        cell.width = Inches(6.5)
        set_cell_margins(cell, 80, 80, 120, 120)
        set_cell_background(cell, "F1F5F9")
        p = cell.paragraphs[0]
        p.paragraph_format.space_after = Pt(0)
        r = p.add_run(code_text)
        r.font.name = "Consolas"
        r.font.size = Pt(9)
        r.font.color.rgb = RGBColor(15, 23, 42)
        doc.add_paragraph().paragraph_format.space_after = Pt(4)

    def add_callout(text, title="CATATAN PENTING"):
        tbl = doc.add_table(rows=1, cols=1)
        tbl.alignment = WD_TABLE_ALIGNMENT.CENTER
        cell = tbl.rows[0].cells[0]
        cell.width = Inches(6.5)
        set_cell_margins(cell, 90, 90, 130, 130)
        set_cell_background(cell, "EFF6FF")
        p = cell.paragraphs[0]
        p.paragraph_format.space_after = Pt(0)
        rt = p.add_run(f"[{title}] ")
        rt.font.name = "Segoe UI"
        rt.font.size = Pt(9.5)
        rt.font.bold = True
        rt.font.color.rgb = COLOR_ACCENT

        rc = p.add_run(text)
        rc.font.name = "Segoe UI"
        rc.font.size = Pt(9.5)
        rc.font.color.rgb = COLOR_PRIMARY
        doc.add_paragraph().paragraph_format.space_after = Pt(4)

    # 1. PENGENALAN
    add_h1("1. Pengenalan & Gambaran Umum")
    add_p("WinVox ExamBro adalah perangkat lunak peramban ujian aman (secure exam browser) berbasis native Windows Presentation Foundation (WPF) dan Microsoft Edge WebView2. Aplikasi ini dirancang khusus untuk mengunci sistem operasi Windows 10 dan Windows 11 selama pelaksanaan ujian berbasis komputer (Computer-Based Testing / CBT).")
    add_p("Perangkat lunak ini dikembangkan dan dikelola oleh Erick Kresnamurti untuk memenuhi kebutuhan integritas pelaksanaan evaluasi belajar di lingkungan sekolah, madrasah, dan perguruan tinggi, baik dalam jaringan intranet lokal (LAN) maupun server berbasis cloud.")

    add_bullet(" Mengisolasi workstation siswa secara penuh dari aplikasi eksternal, shortcut sistem, dan menu Windows.", "Tujuan Utama:")
    add_bullet(" Memastikan halaman web ujian hanya dapat diakses melalui peramban resmi dengan verifikasi header dan User-Agent khusus.", "Integritas Sesi:")
    add_bullet(" Bekerja 100% tanpa ketergantungan internet publik, mendukung server lokal mandiri, dan dapat didistribusikan tanpa proses instalasi yang rumit.", "Fleksibilitas Operasional:")

    # 2. FITUR UTAMA
    add_h1("2. Fitur Utama & Keunggulan Keamanan")
    add_bullet(" Menggunakan jendela maximized borderless yang menutupi seluruh layar desktop dan taskbar pada semua monitor aktif, berstatus Topmost permanen.", "Penguncian Kiosk Penuh:")
    add_bullet(" Low-level Windows Hook (WH_KEYBOARD_LL) memblokir tombol Windows (kiri/kanan), Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4, Ctrl+Shift+Esc, PrintScreen, Menu Apps, dan shortcut browser.", "Pemblokiran Keyboard Tingkat Rendah:")
    add_bullet(" Mencegah interupsi Ctrl+Alt+Del menggunakan Keyboard Filter (WEKF) pada edisi Windows yang mendukung, serta auto-dismiss aktif bila layar Winlogon muncul.", "Filter Kernel & SAS:")
    add_bullet(" Menggunakan SetWinEventHook(EVENT_SYSTEM_FOREGROUND) dan AttachThreadInput untuk segera merebut kembali fokus jika jendela asing mencoba tampil ke depan.", "Penjaga Fokus Aktif (Focus Guard):")
    add_bullet(" Memanfaatkan Windows API hardware-level SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) sehingga perekaman layar oleh OBS, Snipping Tool, Game Bar, Discord, atau Zoom menghasilkan tangkapan hitam pekat.", "Proteksi Anti Tangkapan Layar:")
    add_bullet(" Profil InPrivate mandiri di %LocalAppData%, DevTools dinonaktifkan, klik kanan dimatikan, tombol akselerator peramban mati, download diblokir otomatis, dan bypass otomatis untuk sertifikat SSL lokal self-signed.", "Hardening WebView2:")
    add_bullet(" Scanner background memantau proses terlarang (browser umum, remote desktop, cheat tool, sniffer paket) setiap 1 detik dengan pengecualian proses internal WebView2.", "Pemindai Proses Terlarang:")
    add_bullet(" Kebijakan HKCU registry sementara untuk menonaktifkan Task Manager (DisableTaskMgr), Lock Workstation (DisableLockWorkstation), Change Password (DisableChangePassword), dan Logoff (NoLogoff).", "Penguncian Registry Pengguna:")
    add_bullet(" Komunikasi JavaScript dua arah (window.LouisVoxExamApp), auto-close saat penyerahan jawaban terdeteksi di rute selesai, dan pembukaan kunci jarak jauh (Remote Unlock).", "Integrasi Native projectCBT:")
    add_bullet(" Keluar manual memerlukan kombinasi tombol tiga jari Ctrl + C + B dan verifikasi kata sandi pengawas berbasis PBKDF2-HMAC-SHA256 (310.000 iterasi) dengan pelindung brute-force 5 kali salah.", "Keluar Aman Terproteksi:")

    # 3. ARSITEKTUR
    add_h1("3. Arsitektur Solusi & Struktur Repositori")
    add_p("Proyek ini dibangun menggunakan struktur multi-proyek .NET 8:")
    add_bullet(" Class Library (.NET 8). Mengelola konstanta, model konfigurasi, enkripsi AES-256-GCM, DPAPI, password hashing PBKDF2, pencocokan pola URL wildcard, logger Serilog, dan state recovery.", "ExamBro.Core:")
    add_bullet(" WPF Application (.NET 8 x64). Aplikasi peramban ujian siswa yang meminta hak akses Administrator penuh (requireAdministrator). Berisi service Win32 Kiosk, Hook keyboard, Focus guard, WebView2 hardening, dan antarmuka kiosk.", "ExamBro.Exam:")
    add_bullet(" WPF Application (.NET 8 x64). Aplikasi panel konfigurasi proktor dan administrator berhak akses Administrator penuh. Berisi antarmuka manajemen 6 tab, wisaya awal, pengujian Start URL, dan ekspor/impor konfigurasi.", "ExamBro.Config:")
    add_bullet(" xUnit Test Suite (.NET 8). Memuat 107 pengujian otomatis untuk validasi konfigurasi, pencocokan URL, ketahanan password hashing, dan enkripsi.", "ExamBro.Tests:")
    add_bullet(" Skrip Inno Setup Compiler 6 untuk menghasilkan berkas instalasi mandiri berformat .exe dengan pengaturan ACL folder ProgramData.", "installer/setup.iss:")

    # 4. PRASYARAT SISTEM
    add_h1("4. Prasyarat Sistem & Kebutuhan Perangkat")
    add_p("Kebutuhan Komputer Siswa / Klien Ujian:")
    add_bullet(" Windows 10 (versi 1809 ke atas) atau Windows 11 (64-bit / x64).", "Sistem Operasi:")
    add_bullet(" Hak Administrator lokal (UAC elevation) diperlukan untuk penegakan penguncian desktop.", "Hak Akses:")
    add_bullet(" Microsoft Edge WebView2 Evergreen Runtime (bawaan Windows 10/11 modern).", "WebView2 Runtime:")
    add_bullet(" Tidak memerlukan instalasi runtime .NET tambahan jika menggunakan berkas self-contained single-file.", "Runtime .NET:")
    add_bullet(" Minimal RAM 2 GB (Disarankan 4 GB ke atas), ruang kosong disk 500 MB.", "Perangkat Keras:")

    # 5. KOMPILASI & BUILD
    add_h1("5. Panduan Build & Kompilasi")
    add_p("Untuk membangun binary mandiri dari repositori kode sumber:")
    add_code(
        "# 1. Restore paket NuGet\n"
        "dotnet restore ExamBro.sln\n\n"
        "# 2. Jalankan seluruh unit test (107 tests)\n"
        "dotnet test tests/ExamBro.Tests/ExamBro.Tests.csproj -c Release\n\n"
        "# 3. Publish ExamBro Client (Single-File Mandiri)\n"
        "dotnet publish src/ExamBro.Exam/ExamBro.Exam.csproj \\\n"
        "  -c Release -r win-x64 --self-contained true \\\n"
        "  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \\\n"
        "  -o dist/ExamBro.Exam\n\n"
        "# 4. Publish ExamBro Config Tool (Single-File Mandiri)\n"
        "dotnet publish src/ExamBro.Config/ExamBro.Config.csproj \\\n"
        "  -c Release -r win-x64 --self-contained true \\\n"
        "  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \\\n"
        "  -o dist/ExamBro.Config"
    )

    # 6. TUTORIAL DEPLOYMENT
    add_h1("6. Tutorial Deployment Lengkap")
    add_h2("Metode 1: Mode Portable (Sangat Disarankan)")
    add_p("Metode ini paling cepat dan tidak memerlukan proses instalasi ke sistem operasi Windows:")
    add_bullet(" Siapkan folder portable yang berisi ExamBro.Exam.exe, ExamBro.Config.exe, dan exambro.cfg.", "Langkah 1:")
    add_bullet(" Buka ExamBro.Config.exe di salah satu komputer proktor untuk mengatur Start URL, Whitelist, dan Kata Sandi.", "Langkah 2:")
    add_bullet(" Klik tombol Simpan Portable pada tab Admin. Berkas exambro.cfg terenkripsi mandiri akan dibuat di folder tersebut.", "Langkah 3:")
    add_bullet(" Salin seluruh folder tersebut ke flashdisk atau folder bersama (shared folder) jaringan lokal.", "Langkah 4:")
    add_bullet(" Pada komputer peserta, salin folder ke drive lokal (misalnya D:\\ExamBro\\) lalu jalankan ExamBro.Exam.exe.", "Langkah 5:")

    add_h2("Metode 2: Installer Inno Setup")
    add_p("Metode ini cocok untuk laboratorium komputer sekolah dengan sistem terpusat:")
    add_bullet(" Jalankan ExamBro-Setup-v1.0.0.exe sebagai Administrator.", "Langkah 1:")
    add_bullet(" Program akan terpasang di C:\\Program Files\\ExamBro\\.", "Langkah 2:")
    add_bullet(" Folder data ProgramData dibuat otomatis dengan ACL: Users = Read, Administrators = Full Control.", "Langkah 3:")
    add_bullet(" Shortcut aplikasi akan otomatis dibuat di Desktop dan Start Menu siswa.", "Langkah 4:")

    add_h2("Distribusi Massal di Laboratorium Komputer")
    add_p("Untuk menyebarkan konfigurasi yang sama ke puluhan komputer:")
    add_bullet(" Buat konfigurasi standar pada satu komputer proktor menggunakan ExamBro.Config.exe.", "1:")
    add_bullet(" Salin berkas exambro.cfg yang dihasilkan.", "2:")
    add_bullet(" Tempelkan berkas tersebut ke seluruh komputer klien pada lokasi folder aplikasi atau C:\\ProgramData\\ExamBro\\.", "3:")

    # 7. PANDUAN SISWA
    add_h1("7. Panduan Penggunaan untuk Siswa")
    add_bullet(" Klik kanan ExamBro.Exam.exe lalu pilih Run as administrator (atau klik Yes pada prompt UAC).", "Memulai:")
    add_bullet(" Jendela Pemeriksaan Kesiapan Sistem akan memeriksa integritas workstation.", "Pemeriksaan Kesiapan:")
    add_bullet(" Jika terdeteksi aplikasi terlarang, cukup klik tombol Tutup Aplikasi Terlarang untuk menutup seluruh aplikasi tersebut secara otomatis dalam 1 klik.", "Tutup Otomatis:")
    add_bullet(" Layar akan mengunci ke mode Kiosk layar penuh dan langsung memuat halaman login ujian.", "Mulai Ujian:")
    add_bullet(" Seluruh tombol pintasan sistem dinonaktifkan. Bekerjalah dengan tenang pada halaman ujian.", "Saat Ujian:")
    add_bullet(" Setelah selesai menjawab dan menekan tombol Kirim di web CBT, peramban akan mendeteksi status selesai dan menutup aplikasi secara otomatis.", "Penyelesaian:")
    add_bullet(" Jika harus keluar darurat, panggil pengawas untuk menekan kombinasi Ctrl + C + B dan memasukkan kata sandi pengawas.", "Keluar Darurat:")

    # 8. PANDUAN PENGATURAN PROKTOR
    add_h1("8. Panduan Pengaturan Proktor & Administrator")
    add_p("Jalankan ExamBro.Config.exe sebagai Administrator. Masukkan kata sandi admin (bawaan: admin123).")
    add_bullet(" Tentukan Alamat URL Halaman Awal Ujian (Start URL), Judul Jendela, dan Logo Sekolah (.png / .ico). Gunakan tombol Uji Start URL untuk mengecek keterjangkauan server.", "Tab 1: Umum:")
    add_bullet(" Atur Whitelist URL subnet sekolah (misalnya *192.168.*), Exit URL, dan kontrol audio web CBT (Mute atau batas volume suara listening).", "Tab 2: Navigasi & URL:")
    add_bullet(" Atur proteksi Anti-VM (Verifikasi Perangkat Fisik), Blokir Sesi Remote Desktop, dan penanganan Monitor Ganda (CoverAll atau Block).", "Tab 3: Keamanan:")
    add_bullet(" Kelola daftar hitam proses terlarang. Proses sistem headless terlindung dari false positive.", "Tab 4: Pemantauan Proses:")
    add_bullet(" Ganti kata sandi Administrator dan kata sandi Pengawas (proctor). Ubah tombol pintasan keluar bila diperlukan.", "Tab 5: Akun & Pengawas:")
    add_bullet(" Tentukan direktori penyimpanan berkas log Serilog, webhook HTTPS, dan ekspor/impor berkas konfigurasi .exbcfg.", "Tab 6: Log & Pemulihan:")

    # 9. INTEGRASI PROJECTCBT
    add_h1("9. Integrasi Native dengan projectCBT")
    add_p("WinVox ExamBro kompatibel 100% dengan platform ujian projectCBT tanpa perlu modifikasi backend:")
    add_bullet(" Suffix User-Agent 'ExamBro/1.0.0 ExamBrowser/1.0.0 LouisVox/1.0.0' dan header kustom X-Exambrowser, X-Exambro, X-Exam-Browser, X-CBT-Browser dikenali langsung oleh filter SiteSetting::isExamBrowser.", "Header & User-Agent:")
    add_bullet(" Menginjeksi objek window.LouisVoxExamApp untuk sinkronisasi strike pelanggaran, status kunci, dan auto-release.", "Bridge JavaScript:")
    add_bullet(" Klien berjalan maximized borderless penuh sehingga lolos validasi enforce_fullscreen dan rasio split_screen projectCBT tanpa false positive.", "Pencegahan Alarm Palsu:")
    add_bullet(" Ketika pengawas memasukkan password pada overlay keluar, event blur ditekan sehingga siswa tidak terkena strike palsu.", "Supresi Blur Fokus:")

    # 10. MEKANISME KEAMANAN
    add_h1("10. Mekanisme Keamanan & Hardening")
    add_bullet(" Taskbar Windows disembunyikan menggunakan ShowWindow(SW_HIDE). Jendela kiosk dipatok Topmost permanen.", "Win32 Taskbar:")
    add_bullet(" Hook WH_KEYBOARD_LL memotong penekanan tombol berbahaya sebelum sampai ke message loop aplikasi mana pun.", "Low-Level Hooking:")
    add_bullet(" SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) mengaburkan tangkapan layar menjadi warna hitam pekat.", "Hardware Display Affinity:")
    add_bullet(" HKCU Registry menonaktifkan Task Manager (DisableTaskMgr), Lock Workstation, Change Password, dan opsi Logoff.", "Registry Policies:")
    add_bullet(" Cache dan cookie dibersihkan pada start dan exit. DevTools dan menu klik kanan dinonaktifkan permanen.", "Isolasi WebView2:")
    add_bullet(" Event ServerCertificateErrorDetected otomatis mengeksekusi AlwaysAllow untuk sertifikat self-signed lokal.", "Bypass SSL Lokal:")

    # 11. KETAHANAN MATI LAMPU & CRASH RECOVERY
    add_h1("11. Ketahanan Mati Lampu & Pemulihan Pasca Gangguan")
    add_p("Aplikasi dirancang tahan terhadap insiden mati listrik mendadak di laboratorium:")
    add_bullet(" Jika komputer mati mendadak saat mutex tunggal aktif, peluncuran berikutnya otomatis mendeteksi abandoned mutex dan mengambil alih kepemilikan tanpa error.", "Penanganan Abandoned Mutex:")
    add_bullet(" File SingletonLock dari Chromium dibersihkan secara proaktif sebelum WebView2 dimuat, dengan flag --disable-session-crashed-bubble aktif.", "Pembersihan Lockfile:")
    add_bullet(" Nilai registri asli dan status taskbar tersimpan di %LocalAppData%\\LouisVox ExamBro\\recovery.json. Buka ExamBro berikutnya untuk pemulihan otomatis.", "Recovery State Otomatis:")
    add_bullet(" Buka Command Prompt Administrator lalu ketik: ExamBro.Exam.exe --restore untuk memulihkan taskbar dan registri secara manual seketika.", "Perintah Pemulihan Manual (--restore):")
    add_bullet(" Gunakan perintah ExamBro.Exam.exe --dev-safe untuk menguji aplikasi tanpa mengunci workstation developer.", "Mode Uji Coba Developer (--dev-safe):")

    # 12. PEMECAHAN MASALAH
    add_h1("12. Pemecahan Masalah (Troubleshooting)")
    add_bullet(" Jalankan 'ExamBro.Exam.exe --restore' dari Command Prompt Administrator. Taskbar dan Task Manager akan pulih seketika.", "Taskbar Hilang Pasca Mati Listrik:")
    add_bullet(" Pastikan berkas exambro.cfg berada di folder yang sama dengan ExamBro.Exam.exe. Jika hilang, buka ExamBro.Config.exe untuk membuat konfigurasi baru.", "Konfigurasi Tidak Valid:")
    add_bullet(" Klik kanan aplikasi lalu pilih 'Run as administrator'.", "Peringatan Wajib Administrator:")
    add_bullet(" Periksa kabel jaringan/Wi-Fi, pastikan IP server dapat di-ping, dan pastikan Start URL pada ExamBro.Config.exe telah sesuai.", "Server Ujian Tidak Dapat Dijangkau:")
    add_bullet(" Matikan opsi 'Verifikasi Perangkat Fisik (Deteksi & Blokir Mesin Virtual)' pada Tab Keamanan di ExamBro.Config.exe.", "Gagal Deteksi Mesin Virtual:")

    # 13. REFERENSI CEPAT
    add_h1("13. Referensi Cepat & Nilai Bawaan")

    ref_table = doc.add_table(rows=11, cols=3)
    ref_table.alignment = WD_TABLE_ALIGNMENT.CENTER
    ref_table.autofit = False

    ref_headers = ["Parameter", "Nilai Bawaan", "Keterangan"]
    hdr_row = ref_table.rows[0]
    for j, h in enumerate(ref_headers):
        c = hdr_row.cells[j]
        set_cell_margins(c, 70, 70, 100, 100)
        set_cell_background(c, "1E293B")
        p = c.paragraphs[0]
        p.paragraph_format.space_after = Pt(0)
        r = p.add_run(h)
        r.font.name = "Segoe UI"
        r.font.bold = True
        r.font.size = Pt(9.5)
        r.font.color.rgb = RGBColor(255, 255, 255)

    ref_data = [
        ("Kata Sandi Administrator", "admin123", "Membuka aplikasi ExamBro.Config.exe"),
        ("Kata Sandi Pengawas (Proctor)", "proctor123", "Membuka layar terkunci dan keluar manual"),
        ("Kombinasi Keluar Manual", "Ctrl + C + B", "Tekan tombol C lalu B sambil menahan Ctrl"),
        ("Start URL Bawaan", "http://192.168.70.9", "Alamat portal ujian lokal"),
        ("Port Default projectCBT", "8008", "Port web server CBT bawaan"),
        ("Berkas Konfigurasi", "exambro.cfg", "Terenkripsi AES-256-GCM mandiri"),
        ("Berkas Pemulihan Crash", "%LocalAppData%\\LouisVox ExamBro\\recovery.json", "Pencatatan status taskbar & registri"),
        ("Profil WebView2", "%LocalAppData%\\LouisVox ExamBro\\WebView2Profile", "Penyimpanan browser InPrivate terisolasi"),
        ("Toleransi Brute-Force", "5 kali salah", "Penguncian dialog selama 30 detik"),
        ("Perintah Pemulihan Darurat", "ExamBro.Exam.exe --restore", "Pulihkan desktop tanpa membuka kiosk")
    ]

    for i, row_data in enumerate(ref_data):
        row = ref_table.rows[i + 1]
        for j, val in enumerate(row_data):
            c = row.cells[j]
            set_cell_margins(c, 50, 50, 80, 80)
            if i % 2 == 1:
                set_cell_background(c, "F8FAFC")
            else:
                set_cell_background(c, "FFFFFF")
            p = c.paragraphs[0]
            p.paragraph_format.space_after = Pt(0)
            r = p.add_run(val)
            r.font.name = "Segoe UI"
            r.font.size = Pt(9)
            if j == 0:
                r.font.bold = True
            r.font.color.rgb = COLOR_PRIMARY

    # Kolom Lebar
    for row in ref_table.rows:
        row.cells[0].width = Inches(2.0)
        row.cells[1].width = Inches(1.8)
        row.cells[2].width = Inches(2.7)

    doc.add_paragraph().paragraph_format.space_after = Pt(14)

    # 14. KREDIT & HAK CIPTA
    add_h1("14. Kredit, Kepemilikan & Hak Cipta")
    add_p("Perangkat lunak WinVox ExamBro (LouisVox ExamBro) beserta seluruh kode sumber, modul keamanan, dokumentasi teknis, dan arsitektur penguncian kiosk ini dikembangkan dan dikelola secara resmi oleh:")

    credit_tbl = doc.add_table(rows=4, cols=2)
    credit_tbl.alignment = WD_TABLE_ALIGNMENT.CENTER
    credit_info = [
        ("Nama Pengembang / Arsitek", "Erick Kresnamurti"),
        ("Kepemilikan Hak Cipta", "Hak Cipta © 2024-2026 Erick Kresnamurti"),
        ("Status Hak Cipta", "Seluruh hak cipta dilindungi undang-undang (All rights reserved)."),
        ("Repositori Resmi", "https://github.com/erickkucing/WinVoxExamBro")
    ]
    for i, (k, v) in enumerate(credit_info):
        r = credit_tbl.rows[i]
        c0, c1 = r.cells[0], r.cells[1]
        c0.width = Inches(2.4)
        c1.width = Inches(4.1)
        set_cell_margins(c0, 60, 60, 100, 100)
        set_cell_margins(c1, 60, 60, 100, 100)
        set_cell_background(c0, "EFF6FF")
        set_cell_background(c1, "F8FAFC")
        p0 = c0.paragraphs[0]
        p0.paragraph_format.space_after = Pt(0)
        r0 = p0.add_run(k)
        r0.font.name = "Segoe UI"
        r0.font.bold = True
        r0.font.size = Pt(9.5)
        r0.font.color.rgb = COLOR_SECONDARY

        p1 = c1.paragraphs[0]
        p1.paragraph_format.space_after = Pt(0)
        r1 = p1.add_run(v)
        r1.font.name = "Segoe UI"
        r1.font.size = Pt(9.5)
        r1.font.color.rgb = COLOR_PRIMARY

    doc.save(filepath)
    print(f"Selesai membuat dokumen Word: {filepath}")


# ==============================================================================
# NUMBERED CANVAS FOR PDF
# ==============================================================================
class NumberedCanvas(canvas.Canvas):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._saved_page_states = []

    def showPage(self):
        self._saved_page_states.append(dict(self.__dict__))
        self._startPage()

    def save(self):
        num_pages = len(self._saved_page_states)
        for state in self._saved_page_states:
            self.__dict__.update(state)
            self.draw_page_decorations(num_pages)
            super().showPage()
        super().save()

    def draw_page_decorations(self, page_count):
        self.saveState()
        self.setFont("Helvetica", 8)
        self.setFillColor(RL_MUTED)

        # Header (mulai dari halaman 2)
        if self._pageNumber > 1:
            self.drawString(54, 800, "WinVox ExamBro - Panduan Resmi & Dokumentasi Teknis")
            self.setStrokeColor(RL_BORDER)
            self.setLineWidth(0.5)
            self.line(54, 792, 541, 792)

        # Footer (semua halaman)
        self.setStrokeColor(RL_BORDER)
        self.setLineWidth(0.5)
        self.line(54, 45, 541, 45)

        self.drawString(54, 32, "Hak Cipta © 2024-2026 Erick Kresnamurti. Seluruh hak cipta dilindungi.")
        page_str = f"Halaman {self._pageNumber} dari {page_count}"
        self.drawRightString(541, 32, page_str)

        self.restoreState()


def build_pdf(filepath):
    print(f"Membuat dokumen PDF: {filepath}...")
    doc = SimpleDocTemplate(
        filepath,
        pagesize=A4,
        leftMargin=54,
        rightMargin=54,
        topMargin=54,
        bottomMargin=54,
        title="WinVox ExamBro - Panduan Lengkap & Deployment",
        author="Erick Kresnamurti",
        subject="Dokumentasi Teknis dan Panduan Penggunaan",
        creator="Erick Kresnamurti"
    )

    styles = getSampleStyleSheet()

    # Custom styles
    style_cover_title = ParagraphStyle(
        'CoverTitle',
        parent=styles['Normal'],
        fontName='Helvetica-Bold',
        fontSize=24,
        leading=28,
        textColor=RL_PRIMARY,
        spaceAfter=6
    )

    style_cover_sub = ParagraphStyle(
        'CoverSub',
        parent=styles['Normal'],
        fontName='Helvetica',
        fontSize=12,
        leading=16,
        textColor=RL_SECONDARY,
        spaceAfter=14
    )

    style_h1 = ParagraphStyle(
        'Heading1Custom',
        parent=styles['Heading1'],
        fontName='Helvetica-Bold',
        fontSize=14,
        leading=18,
        textColor=RL_SECONDARY,
        spaceBefore=14,
        spaceAfter=6,
        keepWithNext=True
    )

    style_h2 = ParagraphStyle(
        'Heading2Custom',
        parent=styles['Heading2'],
        fontName='Helvetica-Bold',
        fontSize=11,
        leading=15,
        textColor=RL_PRIMARY,
        spaceBefore=10,
        spaceAfter=4,
        keepWithNext=True
    )

    style_body = ParagraphStyle(
        'BodyCustom',
        parent=styles['Normal'],
        fontName='Helvetica',
        fontSize=9.5,
        leading=13.5,
        textColor=RL_PRIMARY,
        spaceAfter=5
    )

    style_bullet = ParagraphStyle(
        'BulletCustom',
        parent=styles['Normal'],
        fontName='Helvetica',
        fontSize=9.5,
        leading=13.5,
        textColor=RL_PRIMARY,
        leftIndent=15,
        firstLineIndent=-10,
        spaceAfter=3
    )

    style_code = ParagraphStyle(
        'CodeCustom',
        parent=styles['Normal'],
        fontName='Courier',
        fontSize=8.5,
        leading=11.5,
        textColor=RL_PRIMARY
    )

    story = []

    # Title & Metadata
    story.append(Spacer(1, 10))
    story.append(Paragraph("WinVox ExamBro", style_cover_title))
    story.append(Paragraph("Panduan Lengkap, Deployment, dan Dokumentasi Teknis Sistem Browser Ujian Kiosk Windows 10 & 11", style_cover_sub))
    story.append(HRFlowable(width="100%", thickness=1.5, color=RL_ACCENT, spaceBefore=0, spaceAfter=10))

    meta_rows = [
        [Paragraph("<b>Pengembang & Hak Cipta</b>", style_body), Paragraph("Erick Kresnamurti", style_body)],
        [Paragraph("<b>Versi Aplikasi</b>", style_body), Paragraph("1.0.0 (Release Native x64)", style_body)],
        [Paragraph("<b>Kerangka Kerja</b>", style_body), Paragraph(".NET 8.0 WPF, Microsoft Edge WebView2 Evergreen", style_body)],
        [Paragraph("<b>Pembaruan Terakhir</b>", style_body), Paragraph("Tahun 2024 - 2026", style_body)]
    ]
    t_meta = Table(meta_rows, colWidths=[150, 337])
    t_meta.setStyle(TableStyle([
        ('BACKGROUND', (0, 0), (0, -1), colors.HexColor("#f1f5f9")),
        ('BACKGROUND', (1, 0), (1, -1), colors.HexColor("#f8fafc")),
        ('BOX', (0, 0), (-1, -1), 0.5, RL_BORDER),
        ('INNERGRID', (0, 0), (-1, -1), 0.5, RL_BORDER),
        ('TOPPADDING', (0, 0), (-1, -1), 4),
        ('BOTTOMPADDING', (0, 0), (-1, -1), 4),
        ('LEFTPADDING', (0, 0), (-1, -1), 8),
        ('RIGHTPADDING', (0, 0), (-1, -1), 8),
    ]))
    story.append(t_meta)
    story.append(Spacer(1, 12))

    def p_h1(text):
        story.append(Paragraph(text, style_h1))
        story.append(HRFlowable(width="100%", thickness=0.5, color=RL_BORDER, spaceBefore=2, spaceAfter=6))

    def p_h2(text):
        story.append(Paragraph(text, style_h2))

    def p_body(text):
        story.append(Paragraph(text, style_body))

    def p_bullet(label, text):
        full = f"&bull; <b>{label}</b> {text}"
        story.append(Paragraph(full, style_bullet))

    def p_code(code_str):
        # Format code block into table
        lines = code_str.strip().replace("\n", "<br/>").replace(" ", "&nbsp;")
        t = Table([[Paragraph(lines, style_code)]], colWidths=[487])
        t.setStyle(TableStyle([
            ('BACKGROUND', (0, 0), (-1, -1), colors.HexColor("#f1f5f9")),
            ('BOX', (0, 0), (-1, -1), 0.5, RL_BORDER),
            ('TOPPADDING', (0, 0), (-1, -1), 6),
            ('BOTTOMPADDING', (0, 0), (-1, -1), 6),
            ('LEFTPADDING', (0, 0), (-1, -1), 8),
            ('RIGHTPADDING', (0, 0), (-1, -1), 8),
        ]))
        story.append(Spacer(1, 3))
        story.append(t)
        story.append(Spacer(1, 4))

    # Bab 1
    p_h1("1. Pengenalan & Gambaran Umum")
    p_body("WinVox ExamBro adalah solusi peramban ujian aman (secure exam browser) berbasis native Windows Presentation Foundation (WPF) dan Microsoft Edge WebView2. Aplikasi ini dirancang khusus untuk mengunci sistem operasi Windows 10 dan Windows 11 selama pelaksanaan ujian berbasis komputer (Computer-Based Testing / CBT).")
    p_body("Perangkat lunak ini dikembangkan dan dikelola secara resmi oleh Erick Kresnamurti guna menjamin integritas pelaksanaan ujian di sekolah, madrasah, dan kampus.")
    p_bullet("Tujuan Utama:", "Mengisolasi workstation siswa secara penuh dari aplikasi eksternal, shortcut sistem, dan menu Windows.")
    p_bullet("Integritas Sesi:", "Memastikan halaman web ujian hanya dapat diakses melalui peramban resmi dengan verifikasi header dan User-Agent khusus.")
    p_bullet("Fleksibilitas Operasional:", "Bekerja 100% tanpa ketergantungan internet publik, mendukung server lokal mandiri, dan dapat dijalankan langsung dalam mode portable.")

    # Bab 2
    p_h1("2. Fitur Utama & Keunggulan Keamanan")
    p_bullet("Penguncian Kiosk Penuh:", "Jendela borderless maximized menutupi seluruh layar dan taskbar pada semua monitor aktif, berstatus Topmost permanen.")
    p_bullet("Pemblokiran Keyboard Tingkat Rendah:", "Hook WH_KEYBOARD_LL memblokir tombol Windows (kiri/kanan), Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4, Ctrl+Shift+Esc, PrintScreen, Menu Apps, dan shortcut browser.")
    p_bullet("Filter Kernel & SAS:", "Mencegah interupsi Ctrl+Alt+Del menggunakan Keyboard Filter (WEKF) pada edisi Windows yang mendukung, serta auto-dismiss aktif bila layar Winlogon muncul.")
    p_bullet("Penjaga Fokus Aktif:", "SetWinEventHook(EVENT_SYSTEM_FOREGROUND) dan AttachThreadInput segera merebut kembali fokus jika jendela asing mencoba tampil ke depan.")
    p_bullet("Proteksi Anti Tangkapan Layar:", "Memanfaatkan Windows API hardware-level SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) sehingga perekaman layar oleh OBS, Snipping Tool, Game Bar, Discord, atau Zoom menghasilkan tangkapan hitam pekat.")
    p_bullet("Hardening WebView2:", "Profil InPrivate mandiri di %LocalAppData%, DevTools dimatikan, klik kanan dimatikan, tombol akselerator peramban mati, download diblokir otomatis, dan bypass otomatis untuk sertifikat SSL lokal self-signed.")
    p_bullet("Pemindai Proses Terlarang:", "Scanner background memantau proses terlarang (browser umum, remote desktop, cheat tool, sniffer paket) setiap 1 detik dengan pengecualian proses internal WebView2.")
    p_bullet("Penguncian Registry Pengguna:", "Kebijakan HKCU registry sementara untuk menonaktifkan Task Manager (DisableTaskMgr), Lock Workstation, Change Password, dan Logoff.")
    p_bullet("Integrasi Native projectCBT:", "Komunikasi JavaScript dua arah (window.LouisVoxExamApp), auto-close saat penyerahan jawaban terdeteksi di rute selesai, dan pembukaan kunci jarak jauh (Remote Unlock).")
    p_bullet("Keluar Aman Terproteksi:", "Keluar manual memerlukan kombinasi tombol tiga jari Ctrl + C + B dan verifikasi kata sandi pengawas berbasis PBKDF2-HMAC-SHA256 (310.000 iterasi) dengan pelindung brute-force 5 kali salah.")

    # Bab 3
    p_h1("3. Arsitektur Solusi & Struktur Repositori")
    p_body("Struktur multi-proyek .NET 8:")
    p_bullet("ExamBro.Core:", "Class Library (.NET 8). Mengelola konstanta, model konfigurasi, enkripsi AES-256-GCM, DPAPI, password hashing PBKDF2, pencocokan pola URL wildcard, logger Serilog, dan state recovery.")
    p_bullet("ExamBro.Exam:", "WPF Application (.NET 8 x64). Aplikasi peramban ujian siswa yang meminta hak akses Administrator penuh (requireAdministrator). Berisi service Win32 Kiosk, Hook keyboard, Focus guard, WebView2 hardening, dan antarmuka kiosk.")
    p_bullet("ExamBro.Config:", "WPF Application (.NET 8 x64). Aplikasi panel konfigurasi proktor dan administrator berhak akses Administrator penuh. Berisi antarmuka manajemen 6 tab, wisaya awal, pengujian Start URL, dan ekspor/impor konfigurasi.")
    p_bullet("ExamBro.Tests:", "xUnit Test Suite (.NET 8). Memuat 107 pengujian otomatis untuk validasi konfigurasi, pencocokan URL, ketahanan password hashing, dan enkripsi.")
    p_bullet("installer/setup.iss:", "Skrip Inno Setup Compiler 6 untuk menghasilkan berkas instalasi mandiri berformat .exe dengan pengaturan ACL folder ProgramData.")

    # Bab 4
    p_h1("4. Prasyarat Sistem & Kebutuhan Perangkat")
    p_bullet("Sistem Operasi:", "Windows 10 (versi 1809 ke atas) atau Windows 11 (64-bit / x64).")
    p_bullet("Hak Akses:", "Hak Administrator lokal (UAC elevation) diperlukan untuk penegakan penguncian desktop.")
    p_bullet("WebView2 Runtime:", "Microsoft Edge WebView2 Evergreen Runtime (bawaan Windows 10/11 modern).")
    p_bullet("Runtime .NET:", "Tidak memerlukan instalasi runtime .NET tambahan jika menggunakan berkas self-contained single-file.")
    p_bullet("Perangkat Keras:", "Minimal RAM 2 GB (Disarankan 4 GB ke atas), ruang kosong disk 500 MB.")

    # Bab 5
    p_h1("5. Panduan Build & Kompilasi")
    p_body("Perintah build executable mandiri (win-x64):")
    p_code(
        "# Restore dependensi NuGet\n"
        "dotnet restore ExamBro.sln\n\n"
        "# Jalankan seluruh unit test (107 tests)\n"
        "dotnet test tests/ExamBro.Tests/ExamBro.Tests.csproj -c Release\n\n"
        "# Publish Klien Ujian Siswa (Single-File Mandiri)\n"
        "dotnet publish src/ExamBro.Exam/ExamBro.Exam.csproj -c Release -r win-x64 \\\n"
        "  --self-contained true -p:PublishSingleFile=true -o dist/ExamBro.Exam\n\n"
        "# Publish Alat Konfigurasi Proktor (Single-File Mandiri)\n"
        "dotnet publish src/ExamBro.Config/ExamBro.Config.csproj -c Release -r win-x64 \\\n"
        "  --self-contained true -p:PublishSingleFile=true -o dist/ExamBro.Config"
    )

    # Bab 6
    p_h1("6. Tutorial Deployment Lengkap")
    p_h2("Metode 1: Mode Portable (Sangat Disarankan)")
    p_body("Mode portable adalah cara tercepat untuk mendistribusikan aplikasi tanpa instalasi:")
    p_bullet("Langkah 1:", "Siapkan folder portable yang berisi ExamBro.Exam.exe, ExamBro.Config.exe, dan exambro.cfg.")
    p_bullet("Langkah 2:", "Buka ExamBro.Config.exe di satu komputer proktor untuk mengatur Start URL, Whitelist, dan Kata Sandi.")
    p_bullet("Langkah 3:", "Klik Simpan Portable pada tab Admin. Berkas exambro.cfg terenkripsi mandiri akan dibuat di folder tersebut.")
    p_bullet("Langkah 4:", "Salin folder ke flashdisk atau folder bersama (shared folder) jaringan lokal (LAN).")
    p_bullet("Langkah 5:", "Pada komputer peserta, salin folder ke drive lokal (misal D:\\ExamBro\\) lalu jalankan ExamBro.Exam.exe.")

    p_h2("Metode 2: Installer Inno Setup")
    p_body("Jalankan ExamBro-Setup-v1.0.0.exe sebagai Administrator. Program terpasang di C:\\Program Files\\ExamBro\\ dan membuat folder ProgramData dengan izin akses aman.")

    p_h2("Distribusi Massal di Laboratorium Komputer")
    p_body("Konfigurasikan satu berkas exambro.cfg, lalu salin berkas tersebut ke seluruh komputer klien pada lokasi folder aplikasi atau C:\\ProgramData\\ExamBro\\.")

    # Bab 7
    p_h1("7. Panduan Penggunaan untuk Siswa")
    p_bullet("Memulai:", "Klik kanan ExamBro.Exam.exe lalu pilih Run as administrator.")
    p_bullet("Pemeriksaan Kesiapan:", "Jendela Pemeriksaan Kesiapan Sistem akan memeriksa integritas workstation.")
    p_bullet("Tutup Otomatis:", "Jika ada aplikasi terlarang, klik tombol Tutup Aplikasi Terlarang untuk mematikan seluruh proses ilegal sekaligus.")
    p_bullet("Mulai Ujian:", "Layar mengunci ke mode Kiosk dan memuat halaman login ujian.")
    p_bullet("Saat Ujian:", "Seluruh tombol pintasan dinonaktifkan. Bekerjalah hanya pada halaman ujian.")
    p_bullet("Penyelesaian:", "Klik Kirim Jawaban pada web CBT. Aplikasi akan menutup secara otomatis.")
    p_bullet("Keluar Darurat:", "Panggil pengawas untuk menekan kombinasi Ctrl + C + B dan memasukkan kata sandi pengawas.")

    # Bab 8
    p_h1("8. Panduan Pengaturan Proktor & Administrator")
    p_body("Buka ExamBro.Config.exe sebagai Administrator. Masukkan kata sandi admin (bawaan: admin123).")
    p_bullet("Tab 1: Umum:", "Start URL server sekolah, judul aplikasi, kustomisasi logo sekolah (.png / .ico), dan uji jangkauan Start URL.")
    p_bullet("Tab 2: Navigasi & URL:", "Pola whitelist subnet lokal (*192.168.*), Exit URL, dan kontrol audio web CBT (mute atau pembatasan volume suara listening).")
    p_bullet("Tab 3: Keamanan:", "Proteksi Anti-VM (Verifikasi Perangkat Fisik), Blokir Sesi Remote Desktop, dan penanganan Monitor Ganda (CoverAll/Block).")
    p_bullet("Tab 4: Pemantauan Proses:", "Daftar hitam proses terlarang. Proses sistem headless terlindung dari false positive.")
    p_bullet("Tab 5: Akun & Pengawas:", "Ganti kata sandi Administrator dan kata sandi Pengawas (proctor). Ubah tombol pintasan keluar bila diperlukan.")
    p_bullet("Tab 6: Log & Pemulihan:", "Direktori log Serilog harian, webhook HTTPS, dan ekspor/impor berkas konfigurasi .exbcfg.")

    # Bab 9
    p_h1("9. Integrasi Native dengan projectCBT")
    p_bullet("Header & User-Agent:", "Suffix User-Agent 'ExamBro/1.0.0 ExamBrowser/1.0.0 LouisVox/1.0.0' dan header kustom X-Exambrowser, X-Exambro, X-Exam-Browser, X-CBT-Browser dikenali langsung oleh filter SiteSetting::isExamBrowser.")
    p_bullet("Bridge JavaScript:", "Menginjeksi objek window.LouisVoxExamApp untuk sinkronisasi strike pelanggaran, status kunci, dan auto-release.")
    p_bullet("Pencegahan Alarm Palsu:", "Klien berjalan maximized borderless penuh sehingga lolos validasi enforce_fullscreen dan rasio split_screen projectCBT tanpa false positive.")
    p_bullet("Supresi Blur Fokus:", "Ketika pengawas memasukkan password pada overlay keluar, event blur ditekan sehingga siswa tidak terkena strike palsu.")

    # Bab 10
    p_h1("10. Mekanisme Keamanan & Hardening")
    p_bullet("Win32 Taskbar:", "Taskbar Windows disembunyikan menggunakan ShowWindow(SW_HIDE). Jendela kiosk dipatok Topmost permanen.")
    p_bullet("Low-Level Hooking:", "Hook WH_KEYBOARD_LL memotong penekanan tombol berbahaya sebelum sampai ke message loop aplikasi mana pun.")
    p_bullet("Hardware Display Affinity:", "SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) mengaburkan tangkapan layar menjadi warna hitam pekat.")
    p_bullet("Registry Policies:", "HKCU Registry menonaktifkan Task Manager (DisableTaskMgr), Lock Workstation, Change Password, dan opsi Logoff.")
    p_bullet("Isolasi WebView2:", "Cache dan cookie dibersihkan pada start dan exit. DevTools dan menu klik kanan dinonaktifkan permanen.")
    p_bullet("Bypass SSL Lokal:", "Event ServerCertificateErrorDetected otomatis mengeksekusi AlwaysAllow untuk sertifikat self-signed lokal.")

    # Bab 11
    p_h1("11. Ketahanan Mati Lampu & Pemulihan Pasca Gangguan")
    p_bullet("Penanganan Abandoned Mutex:", "Peluncuran berikutnya otomatis mendeteksi abandoned mutex dan mengambil alih kepemilikan tanpa error bentrok instance.")
    p_bullet("Pembersihan Lockfile:", "File SingletonLock Chromium dibersihkan secara proaktif sebelum WebView2 dimuat, dengan flag --disable-session-crashed-bubble aktif.")
    p_bullet("Recovery State Otomatis:", "Nilai registri asli dan status taskbar tersimpan di %LocalAppData%\\LouisVox ExamBro\\recovery.json. Buka ExamBro berikutnya untuk pemulihan otomatis.")
    p_bullet("Perintah Pemulihan Manual:", "Buka Command Prompt Administrator lalu ketik: ExamBro.Exam.exe --restore untuk memulihkan taskbar dan registri secara manual seketika.")
    p_bullet("Mode Uji Coba Developer:", "Gunakan perintah ExamBro.Exam.exe --dev-safe untuk menguji aplikasi tanpa mengunci workstation developer.")

    # Bab 12
    p_h1("12. Pemecahan Masalah (Troubleshooting)")
    p_bullet("Taskbar Hilang Pasca Mati Listrik:", "Jalankan 'ExamBro.Exam.exe --restore' dari Command Prompt Administrator. Taskbar dan Task Manager akan pulih seketika.")
    p_bullet("Konfigurasi Tidak Valid:", "Pastikan berkas exambro.cfg berada di folder yang sama dengan ExamBro.Exam.exe. Jika hilang, buka ExamBro.Config.exe untuk membuat konfigurasi baru.")
    p_bullet("Peringatan Wajib Administrator:", "Klik kanan aplikasi lalu pilih 'Run as administrator'.")
    p_bullet("Server Ujian Tidak Dapat Dijangkau:", "Periksa kabel jaringan/Wi-Fi, pastikan IP server dapat di-ping, dan pastikan Start URL pada ExamBro.Config.exe telah sesuai.")
    p_bullet("Gagal Deteksi Mesin Virtual:", "Matikan opsi 'Verifikasi Perangkat Fisik (Deteksi & Blokir Mesin Virtual)' pada Tab Keamanan di ExamBro.Config.exe.")

    # Bab 13
    p_h1("13. Referensi Cepat & Nilai Bawaan")
    ref_pdf_data = [
        [Paragraph("<b>Parameter</b>", style_body), Paragraph("<b>Nilai Bawaan</b>", style_body), Paragraph("<b>Keterangan</b>", style_body)],
        [Paragraph("Kata Sandi Administrator", style_body), Paragraph("admin123", style_body), Paragraph("Membuka aplikasi ExamBro.Config.exe", style_body)],
        [Paragraph("Kata Sandi Pengawas (Proctor)", style_body), Paragraph("proctor123", style_body), Paragraph("Membuka layar terkunci & keluar manual", style_body)],
        [Paragraph("Kombinasi Keluar Manual", style_body), Paragraph("Ctrl + C + B", style_body), Paragraph("Tekan C lalu B sambil menahan Ctrl", style_body)],
        [Paragraph("Start URL Bawaan", style_body), Paragraph("http://192.168.70.9", style_body), Paragraph("Alamat portal ujian lokal", style_body)],
        [Paragraph("Port Default projectCBT", style_body), Paragraph("8008", style_body), Paragraph("Port web server CBT bawaan", style_body)],
        [Paragraph("Berkas Konfigurasi", style_body), Paragraph("exambro.cfg", style_body), Paragraph("Terenkripsi AES-256-GCM mandiri", style_body)],
        [Paragraph("Berkas Pemulihan Crash", style_body), Paragraph("recovery.json", style_body), Paragraph("Pencatatan status taskbar & registri", style_body)],
        [Paragraph("Toleransi Brute-Force", style_body), Paragraph("5 kali salah", style_body), Paragraph("Penguncian dialog selama 30 detik", style_body)],
        [Paragraph("Perintah Pemulihan Darurat", style_body), Paragraph("ExamBro.Exam.exe --restore", style_body), Paragraph("Pulihkan desktop tanpa membuka kiosk", style_body)]
    ]
    t_ref = Table(ref_pdf_data, colWidths=[150, 137, 200])
    t_ref.setStyle(TableStyle([
        ('BACKGROUND', (0, 0), (-1, 0), RL_HEADER_BG),
        ('TEXTCOLOR', (0, 0), (-1, 0), colors.white),
        ('BOX', (0, 0), (-1, -1), 0.5, RL_BORDER),
        ('INNERGRID', (0, 0), (-1, -1), 0.5, RL_BORDER),
        ('TOPPADDING', (0, 0), (-1, -1), 3.5),
        ('BOTTOMPADDING', (0, 0), (-1, -1), 3.5),
        ('LEFTPADDING', (0, 0), (-1, -1), 6),
        ('RIGHTPADDING', (0, 0), (-1, -1), 6),
        ('ROWBACKGROUNDS', (0, 1), (-1, -1), [colors.white, colors.HexColor("#f8fafc")]),
    ]))
    story.append(t_ref)
    story.append(Spacer(1, 10))

    # Bab 14
    p_h1("14. Kredit, Kepemilikan & Hak Cipta")
    p_body("Perangkat lunak WinVox ExamBro (LouisVox ExamBro) beserta seluruh kode sumber, modul keamanan, dokumentasi teknis, dan arsitektur penguncian kiosk ini dikembangkan dan dikelola secara resmi oleh:")

    credit_rows = [
        [Paragraph("<b>Nama Pengembang / Arsitek</b>", style_body), Paragraph("Erick Kresnamurti", style_body)],
        [Paragraph("<b>Kepemilikan Hak Cipta</b>", style_body), Paragraph("Hak Cipta © 2024-2026 Erick Kresnamurti", style_body)],
        [Paragraph("<b>Status Hak Cipta</b>", style_body), Paragraph("Seluruh hak cipta dilindungi undang-undang (All rights reserved).", style_body)],
        [Paragraph("<b>Repositori Resmi</b>", style_body), Paragraph("https://github.com/erickkucing/WinVoxExamBro", style_body)]
    ]
    t_credit = Table(credit_rows, colWidths=[160, 327])
    t_credit.setStyle(TableStyle([
        ('BACKGROUND', (0, 0), (0, -1), colors.HexColor("#eff6ff")),
        ('BACKGROUND', (1, 0), (1, -1), colors.HexColor("#f8fafc")),
        ('BOX', (0, 0), (-1, -1), 0.5, RL_BORDER),
        ('INNERGRID', (0, 0), (-1, -1), 0.5, RL_BORDER),
        ('TOPPADDING', (0, 0), (-1, -1), 4),
        ('BOTTOMPADDING', (0, 0), (-1, -1), 4),
        ('LEFTPADDING', (0, 0), (-1, -1), 8),
        ('RIGHTPADDING', (0, 0), (-1, -1), 8),
    ]))
    story.append(t_credit)

    doc.build(story, canvasmaker=NumberedCanvas)
    print(f"Selesai membuat dokumen PDF: {filepath}")


def main():
    base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    docs_dir = os.path.join(base_dir, "docs")
    os.makedirs(docs_dir, exist_ok=True)

    docx_path = os.path.join(docs_dir, "Panduan_Lengkap_WinVoxExamBro.docx")
    pdf_path = os.path.join(docs_dir, "Panduan_Lengkap_WinVoxExamBro.pdf")

    build_docx(docx_path)
    build_pdf(pdf_path)

    print("\nSeluruh dokumentasi berhasil dibuat:")
    print(f"1. {docx_path}")
    print(f"2. {pdf_path}")


if __name__ == "__main__":
    main()
