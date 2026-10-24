# PC System Resource Monitor (CPU, RAM, Disk, Network)

Aplikasi pemantau sumber daya sistem PC real-time berbasis **ASP.NET Core .NET 8**, **SignalR**, dan basis data SQLite (`monitoring.db`). Mendukung pemantauan performa CPU, memori RAM, top 20 proses, disk, dan jaringan secara real-time.

Fitur tambahan: **pencatatan otomatis 5 proses dengan CPU tertinggi** pada saat CPU mencapai ambang batas
dan pada saat CPU turun kembali di bawahnya, lengkap dengan label `namaproses(cpu%)`, panel statistik
proses tersering, dan filter/pagination pada antarmuka web (lihat bagian **7. Log Proses Saat CPU Tinggi**).

### Daftar isi

| Bagian | Isi |
|---|---|
| [1](#1-kompilasi--publish) | Kompilasi & Publish |
| [2](#2-menjalankan-langsung-standalone--development) | Menjalankan langsung (development) |
| [3](#3-deployment-di-windows-iis-internet-information-services) | Deployment di Windows IIS |
| [4](#4-deployment-sebagai-windows-service-sc-create) | Deployment sebagai Windows Service |
| [5](#5-konfigurasi-port-portconf) | Konfigurasi port (`port.conf`) |
| [6](#6-auto-save-saat-cpu-melonjak-lonjakan-100) | Auto-save saat CPU melonjak |
| [7](#7-log-proses-saat-cpu-tinggi) | Log proses saat CPU tinggi (dual trigger) |
| [8](#8-dukungan-multi-platform-windows--linux) | Dukungan multi-platform (Windows & Linux) |
| [9](#9-deployment-di-linux-menggunakan-pm2) | Deployment di Linux dengan PM2 |

---

## 1. Kompilasi & Publish

Untuk menghasilkan file siap rilis (*Universal / Framework-Dependent*), jalankan skrip:

```cmd
create_publish_universal.bat
```

Seluruh berkas binary dan aset web akan otomatis disimpan ke dalam folder:
```
publish\
```

---

## 2. Menjalankan Langsung (Standalone / Development)

### Windows
Jalankan file batch:
```cmd
run_app.bat
```
Atau melalui terminal:
```cmd
cd src
dotnet run
```
Aplikasi dapat diakses melalui browser di: `http://localhost:5055`
(Port dapat diubah melalui file `port.conf` — lihat bagian **5. Konfigurasi Port (`port.conf`)**)

---

## 3. Deployment di Windows IIS (Internet Information Services)

Agar aplikasi dapat mengumpulkan metrik sistem (WMI dan Windows Performance Counters) serta terus mencatat riwayat pemantauan secara nonstop, IIS memerlukan konfigurasi khusus pada **Application Pool Identity** (`LocalSystem`) dan **Start Mode** (`AlwaysRunning`).

### Langkah-langkah Konfigurasi:

#### A. Prasyarat
1. Pasang **.NET 8 Hosting Bundle** (ASP.NET Core Runtime + IIS Support).
2. Pastikan fitur IIS **Application Initialization** sudah aktif di Windows Features (*Internet Information Services -> World Wide Web Services -> Common HTTP Features -> Application Initialization*).

#### B. Konfigurasi Application Pool
1. Buka **IIS Manager** (`inetmgr`).
2. Masuk ke menu **Application Pools** -> Klik **Add Application Pool...**.
   - **Name**: `PCMonitoringPool`
   - **.NET CLR Version**: `No Managed Code`
   - **Managed pipeline mode**: `Integrated`
   - Klik **OK**.
3. Klik kanan pada `PCMonitoringPool` yang baru dibuat -> pilih **Advanced Settings...**:
   - **General -> Start Mode**: Ubah dari `OnDemand` menjadi **`AlwaysRunning`**.
   - **Process Model -> Identity**: Klik tombol `...`, pilih opsi **Built-in account**, lalu pilih **`LocalSystem`**. *(Wajib agar aplikasi memiliki hak akses membaca data WMI & Performance Counter sistem)*.
   - **Process Model -> Idle Time-out (minutes)**: Ubah nilainya menjadi **`0`** *(Mencegah aplikasi masuk mode sleep/shutdown saat tidak ada request web)*.
   - **Recycling -> Regular Time Interval (minutes)**: Set ke **`0`** jika tidak ingin didaur ulang otomatis secara berkala.
   - Klik **OK**.

#### C. Konfigurasi Website / Aplikasi
1. Di panel kiri IIS, klik kanan pada **Sites** -> pilih **Add Website...** (atau klik kanan Default Web Site -> **Add Application...**).
   - **Site name**: `PCMonitoring`
   - **Application pool**: Pilih `PCMonitoringPool` yang sudah diatur di atas.
   - **Physical path**: Arahkan ke folder hasil publish, contoh: `D:\DATA\App software bikinan\PCMonitoringCPURam\publish`.
   - **Port**: Tentukan port yang diinginkan (misalnya port `5055` atau port lain yang belum terpakai).
   - Klik **OK**.
2. Klik website `PCMonitoring` -> pada panel kanan klik **Advanced Settings...**:
   - **Preload Enabled**: Ubah dari `False` menjadi **`True`**.
   - Klik **OK**.
3. Buka browser dan arahkan ke alamat URL/port yang sudah ditentukan.

---

## 4. Deployment sebagai Windows Service (`sc create`)

Jika tidak ingin menggunakan IIS dan ingin aplikasi otomatis berjalan sebagai background service saat Windows menyala:

### A. Mendaftarkan Service
Buka **Command Prompt (CMD)** sebagai **Administrator**, lalu jalankan perintah berikut (sesuaikan path absolut ke file `PCMonitoringCPURam.exe`):

```cmd
sc create PCMonitoring binPath= "\"D:\DATA\App software bikinan\PCMonitoringCPURam\publish\PCMonitoringCPURam.exe\"" start= auto obj= "LocalSystem"
```

> **Catatan Penting:**
> - Parameter `obj= "LocalSystem"` wajib disertakan agar service berjalan dengan hak istimewa *LocalSystem* untuk membaca WMI & Performance Counters.
> - Parameter `start= auto` memastikan service otomatis berjalan saat komputer dihidupkan.
> - Perhatikan spasi setelah tanda sama dengan (contoh: `start= auto`, `obj= "LocalSystem"`).

### B. Menjalankan & Memeriksa Status Service
```cmd
:: Menjalankan service
sc start PCMonitoring

:: Mengecek status service
sc query PCMonitoring
```

### C. Menghentikan & Menghapus Service (Uninstall)
```cmd
:: Menghentikan service
sc stop PCMonitoring

:: Menghapus service dari sistem
sc delete PCMonitoring
```

---

## 5. Konfigurasi Port (`port.conf`)

Port HTTP aplikasi ditentukan oleh file **`port.conf`** yang berada di root repository
(dan otomatis disalin ke folder `publish` saat menjalankan `create_publish_universal.bat`).

### Isi file
```
5055
```

Format yang didukung:

| Ditulis sebagai | Keterangan |
|---|---|
| `5055` | Paling sederhana (satu angka) |
| `port=5055` | Format key=value |
| `port = 5070   # komentar` | Boleh ada spasi dan komentar di akhir baris |
| `# komentar` / `; komentar` / `// komentar` | Baris komentar, diabaikan |

### Urutan penentuan port (fallback otomatis)

| Prioritas | Kondisi | Port yang dipakai |
|---|---|---|
| 1 | `port.conf` ada, isinya valid, dan port bebas | Port dari `port.conf` |
| 2 | `port.conf` **tidak ada / kosong / isinya bukan angka 1-65535** | Port default di C#: **5055** (`PortConfiguration.DefaultPort`) |
| 3 | Port di `port.conf` **sedang dipakai** proses lain | Port default di C#: **5055** |
| 4 | Port default **juga** sedang dipakai | Port bebas pertama setelah 5055 (5056, 5057, ...) |

Port efektif selalu dicatat di log saat aplikasi start, contoh:
```
[PORT] Port HTTP: 5071 (sumber: file port.conf (D:\...\port.conf))
[PORT] Server aktif di http://localhost:5071
```
```
[PORT] Port HTTP: 5055 (sumber: default C# (PortConfiguration.DefaultPort)) - port 5071 dari port.conf sedang dipakai proses lain, memakai port default 5055
[PORT] Server aktif di http://localhost:5055
```

### Lokasi file yang dibaca (berurutan)
1. Folder aplikasi (`AppContext.BaseDirectory`) — dipakai saat jalan sebagai Windows Service / exe hasil publish.
2. Content root (folder `src` saat `dotnet run`).
3. Folder kerja saat ini (*current directory*).
4. Folder induk dari lokasi di atas (maksimal 4 level) — sehingga satu `port.conf` di root repository
   tetap terbaca walau aplikasi dijalankan dari folder `src`.

### Catatan
- Setelah mengubah `port.conf`, **restart** aplikasi (atau `sc stop PCMonitoring` lalu `sc start PCMonitoring`).
- Untuk hasil publish, letakkan `port.conf` di folder yang sama dengan `PCMonitoringCPURam.exe`.
- Saat hosting lewat **IIS**, port ditentukan oleh *binding* website IIS, sehingga isi `port.conf` tidak dipakai
  (ASP.NET Core mengabaikan URL dari kode pada mode in-process).
- Implementasi: `src/Services/PortConfiguration.cs`, dipanggil dari `src/Program.cs` melalui
  `builder.WebHost.UseUrls(...)`.

---

## 6. Auto-Save Saat CPU Melonjak (Lonjakan 100%)

Selain penyimpanan rutin **setiap 1 menit**, aplikasi secara otomatis menyimpan snapshot metrik **1x seketika**
pada saat persentase CPU melewati ambang batas yang ditentukan (default **100%**), tanpa menunggu jadwal per menit.

### Cara kerja:
- Deteksi dilakukan setiap detik melalui *rising edge*, yaitu saat CPU **naik melewati** ambang batas
  (dari di bawah ke atas). Setiap lonjakan CPU baru pasti memicu 1x penyimpanan.
- Bila CPU turun kembali di bawah ambang lalu melonjak lagi, lonjakan baru tersebut **tetap tersimpan**
  walau masih berada di dalam jendela 1 menit yang sama.
- Diterapkan *cooldown* minimal **10 detik** antar auto-save agar CPU yang berkedip di sekitar ambang batas
  tidak membanjiri database.
- Auto-save tidak terjadi dobel dengan penyimpanan rutin: apabila jadwal per menit jatuh pada detik yang sama,
  hanya satu penyimpanan yang dilakukan.

> Auto-save snapshot metrik ini **berbeda** dari pencatatan 5 proses CPU tertinggi (bagian 7).
> Snapshot metrik menyimpan angka CPU/RAM/disk/jaringan ke tabel `MetricsHistory`, sedangkan bagian 7
> menyimpan daftar proses ke tabel `CpuHighProcessLog` dan mencatat **dua** momen (naik dan turun).

### Konfigurasi ambang batas
Nilai ambang dapat diubah melalui `appsettings.json`:

```json
"MetricThresholds": {
  "CpuSpikePercent": 100
}
```

| Nilai | Arti |
|---|---|
| `100` | Simpan saat CPU mencapai 100% (default) |
| `95` | Simpan lebih awal saat CPU mencapai 95% |

Nilai di luar rentang 1-100 akan diabaikan dan digunakan nilai default `100`.

> **Catatan penting soal nilai `100`:** `CpuTotalPercent` dibulatkan ke satu angka desimal
> (`Math.Round(..., 1)`) dan dibatasi maksimum 100. Dengan demikian nilai tepat `100.0` sangat jarang
> tercapai, sehingga ambang `100` membuat fitur ini nyaris tidak pernah terpicu. Untuk pemakaian
> sehari-hari, pertimbangkan nilai `95` atau `98` agar log benar-benar terbentuk. Filter `high`/`low`
> pada tabel log otomatis mengikuti nilai yang dipilih — tidak perlu mengubah kode.

### Melihat riwayat auto-save
Buka tab histori pada antarmuka web, atau query langsung:

```sql
SELECT Timestamp, CpuTotal, RamUsagePercent
FROM MetricsHistory
ORDER BY Id DESC
LIMIT 20;
```

---

## 7. Log Proses Saat CPU Tinggi

Aplikasi otomatis **mencatat 5 process dengan pemakaian CPU tertinggi** pada dua momen berbeda.
Data disimpan di tabel SQLite terpisah bernama `CpuHighProcessLog`.

### Dua jenis baris (dual trigger)

| Jenis baris | Kapan dicatat | Nilai `CpuTotal` |
|---|---|---|
| **CPU naik** | Tepat saat CPU menyentuh / melewati ambang batas (rising edge) | `>= ambang` |
| **CPU turun** | Tepat saat CPU turun kembali di bawah ambang batas (falling edge) | `< ambang` |

Aturan penulisan:
- Setiap kejadian menghasilkan **tepat 1 baris "CPU naik"** dan **tepat 1 baris "CPU turun"**.
- Baris "CPU turun" hanya dibuat apabila lonjakan sebelumnya benar-benar tercatat sebagai baris "CPU naik",
  sehingga tidak pernah ada baris "turun" yang berdiri sendiri.
- Penulisan baris log **dipisah** dari penyimpanan snapshot metrik per menit. Tujuannya agar kejadian
  tidak terlewat ketika jadwal simpan rutin kebetulan jatuh pada detik yang sama dengan lonjakan CPU.
- Cooldown **10 detik** diterapkan pada baris "CPU naik", sehingga CPU yang berkedip di sekitar ambang
  batas (mis. `100% -> 99% -> 100%`) tetap dihitung sebagai satu kejadian.

### Format label process

Setiap kolom process menyimpan **nama process beserta persentase CPU-nya** dengan format
`namaproses(cpu%)`, contoh: `chrome(12.5%)`. Persentase memakai satu angka desimal dan pemisah
desimal titik (invariant culture) agar tampilan konsisten di semua konfigurasi regional Windows.

> **Catatan akurasi:** persentase CPU per process dihitung dari selisih *CPU time* antar sampel,
> dengan selang minimal 0.9 detik. Bila aplikasi baru dijalankan dan belum ada nilai acuan, persentase
> dihitung dari selang pendek yang tersedia (bukan dipaksa `0.0%`), sehingga label pada baris pertama
> tetap berisi angka yang wajar.

### Struktur tabel

| Kolom | Isi |
|---|---|
| `Id` | Nomor urut otomatis |
| `Timestamp` | Tanggal & jam kejadian |
| `CpuTotal` | Persentase CPU total saat kejadian |
| `Process1` | Process peringkat 1, format `namaproses(cpu%)` (mis. `chrome(12.5%)`) |
| `Process2` | Process peringkat 2, format `namaproses(cpu%)` |
| `Process3` | Process peringkat 3, format `namaproses(cpu%)` |
| `Process4` | Process peringkat 4, format `namaproses(cpu%)` |
| `Process5` | Process peringkat 5, format `namaproses(cpu%)` |

> Catatan kompatibilitas: baris lama yang tersimpan sebelum perubahan ini hanya berisi nama process
> tanpa persentase, dan tetap ditampilkan apa adanya.
>
> Tabel `CpuHighProcessStats` (statistik frekuensi) tetap menyimpan **nama process murni** tanpa
> suffix `(cpu%)`, sehingga penghitungan `Rank1Count` dan `TotalCount` tidak terpengaruh format tampilan.

Retensi data **7 hari** ke belakang, dibersihkan otomatis setiap kali ada insert baru.

### Query langsung
```sql
SELECT Timestamp, CpuTotal, Process1, Process2, Process3, Process4, Process5
FROM CpuHighProcessLog
ORDER BY Id DESC
LIMIT 20;
```

### Endpoint API
```http
GET /api/metrics/cpu-high-logs?page=1&pageSize=10&cpu=all
```
Mengembalikan satu halaman data log (default 10 baris per halaman).

Parameter `cpu` menentukan baris yang diambil:

| Nilai `cpu` | Baris yang dikembalikan |
|---|---|
| `all` (default) | Semua baris, baik "CPU naik" maupun "CPU turun" |
| `high` | Hanya baris "CPU naik" (`CpuTotal >= ambang batas`) |
| `low` | Hanya baris "CPU turun" (`CpuTotal < ambang batas`) |

Batas pembeda `high`/`low` **mengikuti** nilai `MetricThresholds:CpuSpikePercent` di `appsettings.json`,
sehingga filter selalu konsisten dengan ambang yang benar-benar dipakai untuk mencatat log.

```http
GET /api/metrics/cpu-high-stats?top=10
```
Mengembalikan statistik process yang paling sering muncul saat CPU tinggi.

> Statistik hanya menghitung baris **"CPU naik"**. Baris "CPU turun" tidak menambah statistik, supaya
> satu kejadian tidak terhitung dua kali. Nama process yang sama dalam satu kejadian (mis. `chrome.exe`
> muncul 3x di peringkat berbeda) juga hanya dihitung **sekali**.

### Tampilan di antarmuka web
Tabel **"Log Proses Saat CPU Tinggi"** berada di **paling bawah** halaman dashboard, dengan kolom
`Waktu` (tanggal & jam gabungan format `dd/MM/yyyy HH:mm:ss`), `CPU`, lalu kolom `#1` s.d. `#5` berisi
nama process beserta persentase CPU-nya dalam format `namaproses(cpu%)`, mis. `chrome(12.5%)`.
Tabel dapat digeser ke samping (`overflow-x: auto`) bila lebar layar tidak cukup.
Data dimuat otomatis saat halaman dibuka dan diperbarui setiap 30 detik, serta dapat dimuat ulang
manual lewat tombol **Muat Ulang**.

Di atas tabel terdapat **dropdown filter** untuk menyaring jenis baris:

| Pilihan | Menampilkan |
|---|---|
| **Show All** | Semua baris |
| **Saat CPU Mencapai Ambang** | Hanya baris "CPU naik" |
| **Saat CPU Turun di Bawah Ambang** | Hanya baris "CPU turun" |

Mengubah filter otomatis kembali ke halaman 1, karena jumlah baris hasil penyaringan bisa berbeda.

### Warna & panah penanda jenis kejadian

Kolom `CPU` memakai badge berwarna dan panah tren supaya jenis kejadian langsung terlihat tanpa
membaca angkanya:

| Tampilan | Arti | Warna |
|---|---|---|
| ▲ `100%` | CPU mencapai ambang batas (baris "CPU naik") | Merah (`bg-danger`) |
| ▼ `56.5%` | CPU turun di bawah ambang batas (baris "CPU turun") | Hijau (`bg-success`) |

Pembeda dihitung dari nilai `CpuTotal` dibandingkan ambang batas: baris dengan `CpuTotal >= ambang`
dianggap "CPU naik" (panah ▲ merah), baris dengan `CpuTotal < ambang` dianggap "CPU turun"
(panah ▼ hijau). Arah panah juga muncul sebagai tooltip saat kursor diarahkan ke badge.

Ambang batas yang dipakai UI diambil dari endpoint `/api/metrics/info` (field
`cpuHighThresholdPercent`) yang nilainya berasal dari `MetricThresholds:CpuSpikePercent`. Jadi bila
ambang diubah ke `95`, baris `96%` otomatis berwarna merah dan baris `80%` berwarna hijau tanpa perlu
mengubah kode frontend.

Contoh respons `/api/metrics/info`:
```json
{
  "machineName": "HUJAN-PETIRPC",
  "osDescription": "Microsoft Windows 10.0.26300",
  "processorCount": 12,
  "totalMemoryBytes": 24959852544,
  "cpuHighThresholdPercent": 100
}
```

Di bawah judul tabel juga ada **legenda kecil** yang menampilkan kedua contoh badge beserta artinya.

### Statistik Process Paling Sering Muncul
Di atas tabel log terdapat dua panel statistik:

| Panel | Keterangan |
|---|---|
| **Top 1** | Process yang paling sering menduduki peringkat **#1** (menampilkan jumlah kali jadi #1) |
| **Top 2** | Process yang paling sering masuk daftar **#1 s/d #5** (menampilkan jumlah total kemunculan) |

Data statistik disimpan di tabel terpisah `CpuHighProcessStats` sehingga **tidak terhapus** walaupun
tabel log sudah dipangkas.

### Pembatasan Jumlah Data
- Tabel `CpuHighProcessLog` dibatasi **maksimal 100 baris**. Saat jumlah baris melebihi 100,
  baris terlama otomatis dihapus setiap kali ada insert baru.
- Tabel `CpuHighProcessStats` **tidak** dibatasi, sehingga statistik historis tetap utuh.

---

## 8. Dukungan Multi-Platform (Windows & Linux)

Aplikasi ini **cross-platform** dan dapat dikompilasi untuk Linux. Seluruh pembacaan metrik inti
memakai API lintas platform atau file virtual `/proc`, dan bagian khusus Windows dilindungi
pemeriksaan `OperatingSystem.IsWindows()`.

### Matriks dukungan fitur

| Fitur | Windows | Linux | Sumber data |
|---|---|---|---|
| CPU total & per-core | ✅ | ✅ | `PerformanceCounter` / `NtQuerySystemInformation` → `/proc/stat` |
| RAM total & terpakai | ✅ | ✅ | `GlobalMemoryStatusEx` → `/proc/meminfo` |
| Disk usage | ✅ | ✅ | `PhysicalDisk` counter → `DriveInfo` |
| Network per adapter | ✅ | ✅ | `NetworkInterface` (lintas platform) |
| Top 20 proses (CPU & RAM) | ✅ | ✅ | `NtQuerySystemInformation` → `Process.GetProcesses()` |
| Log proses saat CPU tinggi | ✅ | ✅ | SQLite (lintas platform) |
| Metrik website IIS (`w3wp`) | ✅ | ❌ | Khusus Windows — di Linux otomatis kosong |

> Pada Linux, kolom metrik IIS tidak ditampilkan datanya karena IIS tidak tersedia di platform tersebut.
> Aplikasi tetap berjalan normal tanpa error.

### Cara publish untuk Linux

```bash
# Framework-dependent (butuh .NET 8 Runtime terpasang di server Linux)
dotnet publish src/PCMonitoringCPURam.csproj -c Release -r linux-x64 --self-contained false -o publish-linux

# Self-contained (tidak butuh .NET Runtime terpasang, ukuran lebih besar)
dotnet publish src/PCMonitoringCPURam.csproj -c Release -r linux-x64 --self-contained true -o publish-linux
```

Hasil publish berisi executable native Linux bernama `PCMonitoringCPURam` (tanpa ekstensi).

> Folder `publish/` dari `create_publish_universal.bat` bersifat **framework-dependent portable**,
> sehingga isinya bisa dipakai di Linux dengan menjalankan `dotnet PCMonitoringCPURam.dll`.
> File `PCMonitoringCPURam.exe` di dalamnya hanya dipakai di Windows dan diabaikan di Linux.

### Menjalankan di Linux
```bash
# Pastikan .NET 8 Runtime terpasang
dotnet --list-runtimes

# Jalankan aplikasi
cd /var/www/pcmonitoring
dotnet PCMonitoringCPURam.dll
```

Akses melalui browser di `http://localhost:5055` (dari mesin yang sama).
Untuk akses dari komputer lain, lihat catatan penting di bawah.

> ### ⚠️ Catatan penting: aplikasi hanya mengikat `localhost`
>
> Alamat bind ditetapkan di kode (`src/Program.cs`):
> ```csharp
> builder.WebHost.UseUrls($"http://localhost:{portSelection.Port}");
> ```
> Akibatnya aplikasi **hanya menerima koneksi dari mesin itu sendiri**. Parameter `--urls` dan
> variabel lingkungan `ASPNETCORE_URLS` **diabaikan** karena `UseUrls` di kode menimpa keduanya.
>
> Ini sudah diuji: menjalankan aplikasi dengan `--urls http://0.0.0.0:5077` maupun
> `ASPNETCORE_URLS=http://0.0.0.0:5077` tetap menghasilkan `Now listening on: http://localhost:5055`,
> dan port 5077 tidak pernah terbuka.
>
> Bila ingin diakses dari komputer lain di jaringan, ubah baris tersebut menjadi:
> ```csharp
> builder.WebHost.UseUrls($"http://0.0.0.0:{portSelection.Port}");
> ```
> lalu publish ulang. Sesuaikan juga aturan firewall pada port yang dipakai.
>
> Catatan: perilaku ini tidak spesifik Linux — di Windows pun aplikasi hanya bisa diakses dari
> mesin lokal, kecuali di-hosting lewat **IIS** (port ditentukan binding IIS) atau diubah manual.

### Catatan verifikasi

Yang sudah diverifikasi langsung pada proyek ini:

| Aspek | Status |
|---|---|
| `dotnet publish -r linux-x64` berhasil tanpa error | ✅ Terverifikasi |
| Native library SQLite untuk Linux tersedia (`runtimes/linux-x64/native/libe_sqlite3.so`) | ✅ Terverifikasi |
| Bagian khusus Windows terlindungi `OperatingSystem.IsWindows()` | ✅ Terverifikasi (pembacaan kode) |
| Perilaku `--urls` / `ASPNETCORE_URLS` diabaikan | ✅ Terverifikasi (uji jalan aplikasi) |
| Aplikasi benar-benar dijalankan di mesin Linux | ⚠️ Belum diuji — perlu pengujian di server Linux |

Karena belum diuji langsung di Linux, disarankan melakukan uji coba di server target sebelum
dipakai produksi, terutama untuk akurasi pembacaan CPU/RAM per-core dan daftar proses.

---

## 9. Deployment di Linux Menggunakan PM2

### A. Prasyarat di Linux
1. Pasang **.NET 8 Runtime** di Linux (Ubuntu/Debian/CentOS).
2. Pasang **Node.js** dan **PM2**:
   ```bash
   sudo npm install -g pm2
   ```

### B. Menjalankan Aplikasi dengan PM2
Salin isi folder `publish` ke server Linux (misalnya ke `/var/www/pcmonitoring`), kemudian jalankan:

```bash
# Pindah ke direktori publish
cd /var/www/pcmonitoring

# Jalankan aplikasi menggunakan PM2
pm2 start "dotnet PCMonitoringCPURam.dll" --name "pc-monitoring"
```

> **Jangan** memakai `--urls http://0.0.0.0:5055` pada perintah di atas. Argumen tersebut **diabaikan**
> karena alamat bind sudah ditetapkan lewat `UseUrls` di kode. Bila aplikasi perlu diakses dari
> komputer lain, ubah `src/Program.cs` menjadi `http://0.0.0.0:{port}` lalu publish ulang
> (lihat catatan pada bagian **8. Dukungan Multi-Platform**).

### C. Konfigurasi Autostart Saat Reboot
Agar PM2 otomatis menyalakan aplikasi ketika server Linux restart:

```bash
# Simpan daftar proses PM2 saat ini
pm2 save

# Buat dan aktifkan script startup sistem
pm2 startup
```
*(Jalankan perintah yang dimunculkan oleh terminal jika diminta oleh `pm2 startup`)*.

### D. Manajemen Perintah PM2
```bash
# Melihat status proses
pm2 status

# Melihat log real-time
pm2 logs pc-monitoring

# Merestart aplikasi
pm2 restart pc-monitoring

# Menghentikan aplikasi
pm2 stop pc-monitoring

# Menghapus dari daftar PM2
pm2 delete pc-monitoring
```
