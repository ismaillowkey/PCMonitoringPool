using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace PCMonitoringCPURam.Services
{
    /// <summary>
    /// Hasil penentuan port HTTP aplikasi: port yang dipakai, sumbernya, dan catatan bila terjadi fallback
    /// </summary>
    public class PortSelection
    {
        public int Port { get; init; }

        public string Source { get; init; } = "default";

        public string? Note { get; init; }

        /// <summary>
        /// Ringkasan satu baris untuk ditulis ke log
        /// </summary>
        public string Describe()
        {
            string text = $"Port HTTP: {Port} (sumber: {Source})";

            if (!string.IsNullOrWhiteSpace(Note))
            {
                text += $" - {Note}";
            }

            return text;
        }
    }

    /// <summary>
    /// Menentukan port HTTP aplikasi dari file konfigurasi "port.conf".
    /// Urutan prioritas:
    /// 1. Port dari port.conf (bila file ada, isinya valid, dan port sedang tidak dipakai).
    /// 2. Port default yang ditetapkan di kode C# (<see cref="DefaultPort"/>).
    /// 3. Port bebas berikutnya setelah port default (jaring pengaman bila port default juga dipakai).
    /// </summary>
    public static class PortConfiguration
    {
        /// <summary>
        /// Port default yang dipakai bila port.conf tidak ada / tidak valid / port-nya sudah dipakai proses lain
        /// </summary>
        public const int DefaultPort = 5055;

        /// <summary>
        /// Nama file konfigurasi port yang dibaca aplikasi
        /// </summary>
        public const string FileName = "port.conf";

        // Jumlah level folder induk yang ikut ditelusuri saat mencari port.conf.
        // Berguna agar satu file port.conf di root repository tetap terbaca ketika aplikasi
        // dijalankan dengan "dotnet run" dari folder src.
        private const int MaxParentSearchDepth = 4;

        // Jumlah port berurutan yang dicoba setelah port default (jaring pengaman terakhir)
        private const int MaxAutoPortScan = 100;

        /// <summary>
        /// Membaca port.conf lalu memastikan port tersebut benar-benar bisa dipakai
        /// </summary>
        /// <param name="contentRootPath">Folder content root aplikasi (opsional, dipakai sebagai salah satu lokasi pencarian)</param>
        public static PortSelection Resolve(string? contentRootPath = null)
        {
            string fallbackReason;

            // 1. Coba port dari port.conf
            if (TryReadConfiguredPort(contentRootPath, out int configuredPort, out string configPath, out string? readError))
            {
                if (IsPortAvailable(configuredPort))
                {
                    return new PortSelection
                    {
                        Port = configuredPort,
                        Source = $"file {FileName} ({configPath})"
                    };
                }

                // port.conf valid tetapi port-nya dipakai proses lain -> kembali ke port default
                fallbackReason = $"port {configuredPort} dari {FileName} sedang dipakai proses lain, memakai port default {DefaultPort}";
            }
            else if (!string.IsNullOrWhiteSpace(readError))
            {
                fallbackReason = $"{readError}, memakai port default {DefaultPort}";
            }
            else
            {
                fallbackReason = $"{FileName} tidak ditemukan, memakai port default {DefaultPort}";
            }

            // 2. Fallback ke port default di kode C#
            if (IsPortAvailable(DefaultPort))
            {
                return new PortSelection
                {
                    Port = DefaultPort,
                    Source = "default C# (PortConfiguration.DefaultPort)",
                    Note = fallbackReason
                };
            }

            // 3. Jaring pengaman: port default juga dipakai -> cari port bebas berikutnya
            for (int candidate = DefaultPort + 1; candidate <= DefaultPort + MaxAutoPortScan; candidate++)
            {
                if (IsPortAvailable(candidate))
                {
                    return new PortSelection
                    {
                        Port = candidate,
                        Source = "auto (port bebas pertama setelah port default)",
                        Note = $"{fallbackReason}; port default {DefaultPort} juga dipakai proses lain"
                    };
                }
            }

            // 4. Seluruh rentang port habis: tetap pakai port default agar pesan error Kestrel jelas
            return new PortSelection
            {
                Port = DefaultPort,
                Source = "default C# (PortConfiguration.DefaultPort)",
                Note = $"{fallbackReason}; tidak ada port bebas pada rentang {DefaultPort + 1}-{DefaultPort + MaxAutoPortScan}"
            };
        }

        /// <summary>
        /// Mencari file port.conf pada beberapa lokasi yang mungkin (folder aplikasi, content root, folder kerja, folder induk)
        /// </summary>
        public static string? FindConfigFile(string? contentRootPath = null)
        {
            var candidates = new List<string>();

            void AddCandidate(string? directory)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                try
                {
                    string fullPath = Path.Combine(Path.GetFullPath(directory), FileName);

                    if (!candidates.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                    {
                        candidates.Add(fullPath);
                    }
                }
                catch (Exception)
                {
                    // Path tidak valid (misalnya berisi karakter terlarang) -> lewati
                }
            }

            string?[] startDirectories =
            {
                AppContext.BaseDirectory,
                contentRootPath,
                Directory.GetCurrentDirectory()
            };

            // Prioritas utama: lokasi langsung (folder publish/exe, content root, folder kerja)
            foreach (string? startDirectory in startDirectories)
            {
                AddCandidate(startDirectory);
            }

            // Prioritas kedua: folder induk (misalnya src -> root repository)
            foreach (string? startDirectory in startDirectories)
            {
                string? current = startDirectory;

                for (int depth = 0; depth < MaxParentSearchDepth; depth++)
                {
                    try
                    {
                        current = string.IsNullOrWhiteSpace(current) ? null : Path.GetDirectoryName(Path.GetFullPath(current));
                    }
                    catch (Exception)
                    {
                        current = null;
                    }

                    if (string.IsNullOrWhiteSpace(current))
                    {
                        break;
                    }

                    AddCandidate(current);
                }
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // Akses ditolak -> lanjut ke kandidat berikutnya
                }
            }

            return null;
        }

        /// <summary>
        /// Membaca dan memvalidasi nilai port dari port.conf.
        /// Format yang didukung: "5055", "port=5055", "port = 5070   # komentar",
        /// baris komentar diawali #, ; atau //.
        /// </summary>
        private static bool TryReadConfiguredPort(string? contentRootPath, out int port, out string configPath, out string? error)
        {
            port = 0;
            configPath = string.Empty;
            error = null;

            string? filePath = FindConfigFile(contentRootPath);

            if (string.IsNullOrWhiteSpace(filePath))
            {
                // port.conf tidak ada bukan error, aplikasi memakai port default
                return false;
            }

            configPath = filePath;

            string content;

            try
            {
                content = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                error = $"{FileName} tidak bisa dibaca ({ex.Message})";
                return false;
            }

            string? value = ExtractPortValue(content);

            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"{FileName} tidak berisi angka port";
                return false;
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPort)
                || parsedPort < 1
                || parsedPort > 65535)
            {
                error = $"nilai port \"{value}\" pada {FileName} tidak valid (harus angka 1-65535)";
                return false;
            }

            port = parsedPort;
            return true;
        }

        /// <summary>
        /// Mengambil nilai port dari baris pertama yang bukan komentar
        /// </summary>
        private static string? ExtractPortValue(string content)
        {
            foreach (string rawLine in content.Split('\n'))
            {
                // Buang BOM (bila file disimpan sebagai UTF-8 with BOM) dan spasi di ujung baris
                string line = rawLine.Trim().TrimStart('\uFEFF');

                if (line.Length == 0
                    || line.StartsWith("#", StringComparison.Ordinal)
                    || line.StartsWith(";", StringComparison.Ordinal)
                    || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                // Buang komentar di akhir baris, contoh: 5070   # port produksi
                int commentIndex = line.IndexOf('#');

                if (commentIndex >= 0)
                {
                    line = line.Substring(0, commentIndex).Trim();
                }

                // Dukung format "port=5070" maupun "5070"
                int separatorIndex = line.IndexOf('=');

                if (separatorIndex >= 0)
                {
                    line = line.Substring(separatorIndex + 1).Trim();
                }

                line = line.Trim('"', '\'').Trim();

                if (line.Length > 0)
                {
                    return line;
                }
            }

            return null;
        }

        /// <summary>
        /// Memeriksa apakah port masih bebas dengan mencoba mengikat socket pada loopback IPv4 dan IPv6
        /// (cara yang sama dipakai Kestrel saat mengikat alamat "localhost").
        /// </summary>
        private static bool IsPortAvailable(int port)
        {
            if (!TryBindLoopback(IPAddress.Loopback, port))
            {
                return false;
            }

            if (Socket.OSSupportsIPv6 && !TryBindLoopback(IPAddress.IPv6Loopback, port))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Mencoba membuka lalu menutup listener pada satu alamat. Gagal = port sedang dipakai.
        /// </summary>
        private static bool TryBindLoopback(IPAddress address, int port)
        {
            TcpListener? listener = null;

            try
            {
                listener = new TcpListener(address, port);

                // Samakan dengan perilaku Kestrel di Windows yang memakai bind eksklusif,
                // agar deteksi "port sedang dipakai" tidak memberi hasil palsu
                if (OperatingSystem.IsWindows())
                {
                    listener.ExclusiveAddressUse = true;
                }

                listener.Start();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                try
                {
                    listener?.Stop();
                }
                catch (Exception)
                {
                    // Diabaikan: listener sudah ditutup / tidak pernah terbuka
                }
            }
        }
    }
}
