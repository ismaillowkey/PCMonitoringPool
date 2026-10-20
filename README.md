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

## 5. Deployment di Linux Menggunakan PM2

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
