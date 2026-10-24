# PC System Resource Monitor (CPU, RAM, Disk, Network)

Aplikasi pemantau sumber daya sistem PC real-time berbasis **ASP.NET Core .NET 8**, **SignalR**, dan basis data SQLite (`monitoring.db`). Mendukung pemantauan performa CPU, memori RAM, top 20 proses, disk, dan jaringan secara real-time.

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

Pada saat CPU mencapai ambang batas, aplikasi otomatis **mencatat 5 process dengan pemakaian CPU tertinggi**
pada saat kejadian tersebut. Data disimpan di tabel SQLite terpisah bernama `CpuHighProcessLog`.

### Struktur tabel

| Kolom | Isi |
|---|---|
| `Id` | Nomor urut otomatis |
| `Timestamp` | Tanggal & jam kejadian |
| `CpuTotal` | Persentase CPU total saat kejadian |
| `Process1` | Nama process dengan CPU tertinggi (peringkat 1) |
| `Process2` | Nama process peringkat 2 |
| `Process3` | Nama process peringkat 3 |
| `Process4` | Nama process peringkat 4 |
| `Process5` | Nama process peringkat 5 |

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
GET /api/metrics/cpu-high-logs?limit=100
```
Parameter `limit` maksimal 500 baris (default 100).

### Tampilan di antarmuka web
Tabel **"Log Proses Saat CPU Tinggi"** berada di **paling bawah** halaman dashboard, dengan kolom
`Tanggal`, `Jam`, `CPU`, lalu kolom `#1` s.d. `#5` berisi nama process. Data dimuat otomatis saat
halaman dibuka dan diperbarui setiap 30 detik, serta dapat dimuat ulang manual lewat tombol
**Muat Ulang**.

---

## 8. Deployment di Linux Menggunakan PM2

Aplikasi ini mendukung cross-platform (Linux) untuk membaca metrik CPU/RAM melalui interface `/proc`.

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
pm2 start "dotnet PCMonitoringCPURam.dll --urls http://0.0.0.0:5055" --name "pc-monitoring"
```

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
