// PC System Resource Monitor - Vue 3 Frontend Logic (100% Offline, Light Mode, ApexCharts for CPU)
const { createApp, ref, reactive, computed, onMounted } = Vue;

createApp({
    setup() {
        // Status koneksi SignalR
        const connectionStatus = ref('disconnected'); // 'connected', 'reconnecting', 'disconnected'
        const connectionText = ref('Menghubungkan...');

        // Status tampilan legend di samping CPU ApexChart
        const isLegendVisible = ref(true);

        // Status visibilitas checkbox tiap core CPU dan Total CPU
        const coreVisibility = reactive([]);
        const totalVisibility = ref(true);

        // Informasi sistem
        const systemInfo = reactive({
            machineName: '-',
            osDescription: '-',
            processorCount: 0,
            totalMemoryBytes: 0,
            cpuHighThresholdPercent: 100
        });

        // Metrik live terbaru
        const live = reactive({
            timestamp: '-',
            cpuTotalPercent: 0,
            cpuCores: [],
            coreCount: 0,
            ramUsagePercent: 0,
            ramTotalBytes: 0,
            ramUsedBytes: 0,
            ramAvailableBytes: 0,
            diskUsagePercent: 0,
            appDiskName: 'Drive D:',
            disks: [],
            networkUsagePercent: 0,
            networkBytesPerSec: 0,
            networkAdapters: [],
            iisWebsites: []
        });

        // Pilihan dropdown Network adapter aktif
        const selectedNetworkName = ref('_Total');

        // Computed property nama adapter terpilih tanpa ternary operator
        const selectedNetworkDisplayName = computed(function() {
            if (selectedNetworkName.value === '_Total') {
                return 'Total Network';
            }
            return selectedNetworkName.value;
        });

        // Nilai persentase penggunaan network yang dipilih pengguna pada dropdown
        const currentSelectedNetworkUsage = computed(function() {
            if (selectedNetworkName.value === '_Total') {
                return live.networkUsagePercent;
            }
            if (live.networkAdapters.length > 0) {
                for (let i = 0; i < live.networkAdapters.length; i++) {
                    if (live.networkAdapters[i].name === selectedNetworkName.value) {
                        return live.networkAdapters[i].usagePercent;
                    }
                }
            }
            return live.networkUsagePercent;
        });

        // Nilai kecepatan bytes/detik network yang dipilih
        const currentSelectedNetworkSpeed = computed(function() {
            if (selectedNetworkName.value === '_Total') {
                return live.networkBytesPerSec;
            }
            if (live.networkAdapters.length > 0) {
                for (let i = 0; i < live.networkAdapters.length; i++) {
                    if (live.networkAdapters[i].name === selectedNetworkName.value) {
                        return live.networkAdapters[i].bytesPerSec;
                    }
                }
            }
            return live.networkBytesPerSec;
        });

        // Computed property untuk info sistem di header secara eksplisit tanpa operator ternary
        const displayMachineName = computed(function() {
            if (systemInfo.machineName && systemInfo.machineName !== '-') {
                return systemInfo.machineName;
            }
            return 'Local PC';
        });

        const displayOsDescription = computed(function() {
            if (systemInfo.osDescription && systemInfo.osDescription !== '-') {
                return systemInfo.osDescription;
            }
            return 'Windows OS';
        });

        const displayCoreCount = computed(function() {
            if (systemInfo.processorCount > 0) {
                return systemInfo.processorCount;
            }
            if (live.coreCount > 0) {
                return live.coreCount;
            }
            return 12;
        });

        const displayTotalRam = computed(function() {
            if (live.ramTotalBytes > 0) {
                return formatBytes(live.ramTotalBytes);
            }
            if (systemInfo.totalMemoryBytes > 0) {
                return formatBytes(systemInfo.totalMemoryBytes);
            }
            return '-';
        });

        // Opsi histori
        const selectedRange = ref('1h');
        const selectedMetric = ref('cpu'); // 'cpu', 'ram', 'disk', 'network', 'all'
        const isLoadingHistory = ref(false);
        const historyRecords = ref([]);

        // State Top 20 Program CPU High Usage & High RAM
        const topProcesses = reactive({
            topCpu: [],
            topRam: []
        });
        const isLoadingTopProcesses = ref(false);

        // State log proses saat CPU mencapai ambang batas tinggi (5 process tertinggi)
        const cpuHighLogs = ref([]);
        const isLoadingCpuHighLogs = ref(false);

        // State pagination untuk tabel log CPU tinggi (10 baris per halaman)
        const cpuHighLogPage = ref(1);
        const cpuHighLogPageSize = 10;
        const cpuHighLogTotal = ref(0);
        const cpuHighLogTotalPages = ref(0);

        // Filter tabel log CPU tinggi: 'all' (semua), 'high' (saat CPU 100%), 'low' (saat CPU < 100%)
        const cpuHighLogFilter = ref('all');

        // State statistik process yang paling sering muncul saat CPU tinggi
        const cpuHighStats = reactive({
            top1: [],
            topAny: [],
            totalEvents: 0
        });

        // Objek instance chart dan koneksi SignalR
        let cpuApexChart = null;
        let ramLiveChart = null;
        let diskLiveChart = null;
        let netLiveChart = null;
        let iisCpuLiveChart = null;
        let iisMemLiveChart = null;
        let historyChart = null;
        let signalRConnection = null;

        // Data series untuk live chart IIS
        const iisTimeCategories = [];
        const iisCpuSeriesData = {};
        const iisMemSeriesData = {};
        const IIS_MAX_LIVE_POINTS = 30;
        const IIS_PALETTE = [
            '#0284c7', // Sky Blue
            '#7c3aed', // Violet
            '#059669', // Emerald
            '#d97706', // Amber
            '#dc2626', // Red
            '#ea580c', // Orange
            '#0891b2', // Cyan
            '#4f46e5', // Indigo
            '#db2777', // Pink
            '#16a34a'  // Green
        ];

        // Status visibilitas series pada grafik Histori (dengan kontrol Checkbox)
        const historyCoreVisibility = reactive([]);
        const historyTotalVisibility = ref(true);
        const historyAllVisibility = reactive({
            cpu: true,
            ram: true,
            disk: true,
            network: true
        });

        // Inisialisasi default core histori: Total CPU aktif, core 0..63 default false (kotak kosong tanpa coret)
        for (let c = 0; c < 64; c++) {
            historyCoreVisibility[c] = false;
        }

        const MAX_LIVE_POINTS = 30;

        // Buffer data streaming untuk ApexCharts CPU
        const cpuTimeCategories = [];
        const cpuCoreSeriesData = [];
        const cpuTotalSeriesData = [];

        // Warna styling chart untuk Light Mode yang bersih dan kontras
        const CHART_GRID_COLOR = 'rgba(0, 0, 0, 0.06)';
        const CHART_TICK_COLOR = '#64748b';
        const CHART_LEGEND_COLOR = '#334155';

        // Fungsi format ukuran bytes ke format yang mudah dibaca (GB / MB)
        function formatBytes(bytes) {
            if (!bytes || bytes <= 0) {
                return '0 B';
            }
            const gigaBytes = bytes / (1024 * 1024 * 1024);
            if (gigaBytes >= 1.0) {
                return gigaBytes.toFixed(2) + ' GB';
            }
            const megaBytes = bytes / (1024 * 1024);
            return megaBytes.toFixed(1) + ' MB';
        }

        // Fungsi format kecepatan network
        function formatNetworkSpeed(bytesPerSec) {
            if (!bytesPerSec || bytesPerSec <= 0) {
                return '0 KB/s';
            }
            const mbSec = bytesPerSec / (1024 * 1024);
            if (mbSec >= 1.0) {
                return mbSec.toFixed(2) + ' MB/s';
            }
            const kbSec = bytesPerSec / 1024;
            return kbSec.toFixed(1) + ' KB/s';
        }

        // Helper penentuan badge warna persentase CPU tanpa operator ternary
        function getCpuBadgeClass(percent) {
            if (percent >= 50.0) {
                return 'bg-danger text-white';
            }
            if (percent >= 20.0) {
                return 'bg-warning text-dark';
            }
            if (percent >= 5.0) {
                return 'bg-info text-white';
            }
            return 'bg-light text-muted border';
        }

        // Apakah baris log ini adalah kejadian "CPU turun di bawah ambang"?
        // Baris "CPU naik" bernilai >= ambang, baris "CPU turun" bernilai < ambang,
        // jadi ambang batas itu sendiri sudah cukup untuk membedakan keduanya.
        function isCpuDropLog(cpuTotal) {
            let value = Number(cpuTotal);
            if (isNaN(value)) {
                return false;
            }
            let threshold = Number(systemInfo.cpuHighThresholdPercent);
            if (isNaN(threshold) || threshold <= 0) {
                threshold = 100;
            }
            return value < threshold;
        }

        // Badge CPU untuk tabel log:
        //  - kejadian "CPU naik"  -> merah, panah ke atas
        //  - kejadian "CPU turun" -> hijau, panah ke bawah
        // Tanpa operator ternary agar gaya kode tetap konsisten.
        function getCpuLogBadgeClass(cpuTotal) {
            if (isCpuDropLog(cpuTotal)) {
                return 'bg-success text-white';
            }
            return 'bg-danger text-white';
        }

        // Ikon panah tren: ke bawah saat CPU turun, ke atas saat CPU naik
        function getCpuLogTrendIcon(cpuTotal) {
            if (isCpuDropLog(cpuTotal)) {
                return '↓';
            }
            return '↑';
        }

        // Tooltip penjelas supaya makna panah tidak ambigu
        function getCpuLogTrendTitle(cpuTotal) {
            if (isCpuDropLog(cpuTotal)) {
                return 'CPU turun di bawah ambang batas';
            }
            return 'CPU mencapai ambang batas';
        }

        // Helper format kapasitas memori RAM MB / GB tanpa operator ternary
        function formatMb(mb) {
            if (!mb || mb <= 0) {
                return '0 MB';
            }
            if (mb >= 1024.0) {
                const gb = mb / 1024.0;
                return gb.toFixed(2) + ' GB';
            }
            return mb.toFixed(1) + ' MB';
        }

        // Format tanggal & jam gabungan (dd/MM/yyyy HH:mm:ss) untuk tabel log CPU tinggi
        function formatLogDateTime(timestamp) {
            if (!timestamp) {
                return '-';
            }
            const dt = new Date(timestamp);
            if (isNaN(dt.getTime())) {
                return '-';
            }
            const day = String(dt.getDate()).padStart(2, '0');
            const month = String(dt.getMonth() + 1).padStart(2, '0');
            const year = dt.getFullYear();
            const hour = String(dt.getHours()).padStart(2, '0');
            const minute = String(dt.getMinutes()).padStart(2, '0');
            const second = String(dt.getSeconds()).padStart(2, '0');
            return day + '/' + month + '/' + year + ' ' + hour + ':' + minute + ':' + second;
        }

        // Fungsi fetch log proses saat CPU mencapai ambang batas tinggi (dengan pagination)
        let isFetchingCpuHighLogs = false;
        async function fetchCpuHighLogs(page) {
            if (isFetchingCpuHighLogs) {
                return;
            }
            isFetchingCpuHighLogs = true;
            isLoadingCpuHighLogs.value = true;

            // Tentukan halaman yang diminta (default: halaman saat ini)
            let targetPage = cpuHighLogPage.value;
            if (page !== undefined && page !== null && page > 0) {
                targetPage = page;
            }

            try {
                const url = '/api/metrics/cpu-high-logs?page=' + targetPage
                    + '&pageSize=' + cpuHighLogPageSize
                    + '&cpu=' + encodeURIComponent(cpuHighLogFilter.value);
                const response = await fetch(url);
                if (response.ok) {
                    const data = await response.json();
                    if (data && Array.isArray(data.items)) {
                        cpuHighLogs.value = data.items;
                        cpuHighLogPage.value = data.page;
                        cpuHighLogTotal.value = data.totalCount;
                        cpuHighLogTotalPages.value = data.totalPages;
                    }
                }
            } catch (err) {
                console.error('Peringatan fetch log CPU tinggi:', err);
            } finally {
                isFetchingCpuHighLogs = false;
                isLoadingCpuHighLogs.value = false;
            }
        }

        // Navigasi ke halaman tertentu pada tabel log CPU tinggi
        function goToCpuHighLogPage(page) {
            if (page < 1) {
                return;
            }
            if (cpuHighLogTotalPages.value > 0 && page > cpuHighLogTotalPages.value) {
                return;
            }
            fetchCpuHighLogs(page);
        }

        // Mengubah filter tabel log CPU tinggi, lalu memuat ulang dari halaman pertama
        function setCpuHighLogFilter(value) {
            // Hanya tiga nilai yang dikenali sesuai pilihan dropdown
            let newFilter = 'all';
            if (value === 'high') {
                newFilter = 'high';
            }
            else if (value === 'low') {
                newFilter = 'low';
            }

            cpuHighLogFilter.value = newFilter;

            // Kembali ke halaman 1 karena jumlah baris hasil filter bisa berbeda
            fetchCpuHighLogs(1);
        }

        // Fungsi fetch statistik process yang paling sering muncul saat CPU tinggi
        let isFetchingCpuHighStats = false;
        async function fetchCpuHighStats() {
            if (isFetchingCpuHighStats) {
                return;
            }
            isFetchingCpuHighStats = true;

            try {
                const response = await fetch('/api/metrics/cpu-high-stats?top=10');
                if (response.ok) {
                    const data = await response.json();
                    if (data) {
                        cpuHighStats.top1 = data.top1 || [];
                        cpuHighStats.topAny = data.topAny || [];
                        cpuHighStats.totalEvents = data.totalEvents || 0;
                    }
                }
            } catch (err) {
                console.error('Peringatan fetch statistik CPU tinggi:', err);
            } finally {
                isFetchingCpuHighStats = false;
            }
        }

        // Daftar nomor halaman untuk kontrol pagination (maksimal 5 tombol agar rapi)
        const cpuHighLogPageNumbers = computed(function() {
            const total = cpuHighLogTotalPages.value;
            const current = cpuHighLogPage.value;

            if (total <= 0) {
                return [];
            }

            // Tampilkan maksimal 5 nomor halaman di sekitar halaman saat ini
            let start = current - 2;
            if (start < 1) {
                start = 1;
            }

            let end = start + 4;
            if (end > total) {
                end = total;
                start = end - 4;
            }

            if (start < 1) {
                start = 1;
            }

            const pages = [];
            for (let i = start; i <= end; i++) {
                pages.push(i);
            }
            return pages;
        });

        // Fungsi fetch Top 20 Program CPU High Usage dan High RAM
        let isFetchingTopProcesses = false;
        async function fetchTopProcesses() {
            if (isFetchingTopProcesses) {
                return;
            }
            isFetchingTopProcesses = true;
            isLoadingTopProcesses.value = true;

            // Prioritaskan pengambilan via WebSocket SignalR yang stabil dan bebas intersepsi HTTP
            if (signalRConnection && connectionStatus.value === 'connected') {
                try {
                    const data = await signalRConnection.invoke('RequestTopProcesses');
                    if (data && data.topCpu && data.topRam) {
                        topProcesses.topCpu = data.topCpu;
                        topProcesses.topRam = data.topRam;
                        isFetchingTopProcesses = false;
                        isLoadingTopProcesses.value = false;
                        return;
                    }
                } catch (wsErr) {
                    console.warn('Fallback ke fetch HTTP untuk top processes:', wsErr);
                }
            }

            // Jalur sekunder: Fetch via HTTP endpoint
            try {
                const response = await fetch('/api/metrics/top-processes');
                if (response.ok) {
                    const data = await response.json();
                    if (data && data.topCpu && data.topRam) {
                        topProcesses.topCpu = data.topCpu;
                        topProcesses.topRam = data.topRam;
                    }
                }
            } catch (err) {
                console.error('Peringatan fetch top processes:', err);
            } finally {
                isFetchingTopProcesses = false;
                isLoadingTopProcesses.value = false;
            }
        }

        // Generator warna cerah untuk N core CPU di atas canvas putih (Light Mode)
        function generateCoreColor(coreIndex, totalCores) {
            let divisor = 1;
            if (totalCores > 0) {
                divisor = totalCores;
            }
            const hue = Math.round((coreIndex * 360) / divisor);
            return `hsl(${hue}, 85%, 45%)`;
        }

        // Toggle visibility legend samping pada ApexCharts CPU
        function toggleLegend() {
            isLegendVisible.value = !isLegendVisible.value;
            if (cpuApexChart) {
                cpuApexChart.updateOptions({
                    legend: {
                        show: isLegendVisible.value
                    }
                }, false, false);
            }
        }

        // Toggle visibilitas garis core CPU pada ApexCharts
        function toggleCoreSeries(idx) {
            if (coreVisibility[idx] === undefined) {
                coreVisibility[idx] = true;
            }
            coreVisibility[idx] = !coreVisibility[idx];
            if (cpuApexChart) {
                if (coreVisibility[idx]) {
                    cpuApexChart.showSeries('Core ' + idx);
                } else {
                    cpuApexChart.hideSeries('Core ' + idx);
                }
            }
        }

        // Toggle visibilitas garis Total CPU pada ApexCharts
        function toggleTotalSeries() {
            totalVisibility.value = !totalVisibility.value;
            if (cpuApexChart) {
                if (totalVisibility.value) {
                    cpuApexChart.showSeries('Total CPU');
                } else {
                    cpuApexChart.hideSeries('Total CPU');
                }
            }
        }

        // Inisialisasi Chart Live CPU menggunakan ApexCharts Line dengan Legend di Samping
        function initCpuApexChart(coreCount) {
            let finalCoreCount = 12;
            if (coreCount && coreCount > 0) {
                finalCoreCount = coreCount;
            }

            const el = document.getElementById('cpuApexChart');
            if (!el) {
                return;
            }

            // Siapkan series, warna, dan ketebalan garis untuk ApexCharts
            const seriesList = [];
            const colorsList = [];
            const strokeWidthList = [];

            // Reset buffer data
            cpuCoreSeriesData.length = 0;
            cpuTotalSeriesData.length = 0;
            cpuTimeCategories.length = 0;

            for (let i = 0; i < finalCoreCount; i++) {
                if (coreVisibility[i] === undefined) {
                    coreVisibility[i] = true;
                }
                cpuCoreSeriesData[i] = [];
                seriesList.push({
                    name: 'Core ' + i,
                    data: []
                });
                colorsList.push(generateCoreColor(i, finalCoreCount));
                strokeWidthList.push(1.8);
            }

            // Tambahkan Total CPU (garis gelap tegas dan tebal)
            seriesList.push({
                name: 'Total CPU',
                data: []
            });
            colorsList.push('#0f172a');
            strokeWidthList.push(3.5);

            const options = {
                series: seriesList,
                chart: {
                    type: 'line',
                    height: 270,
                    animations: {
                        enabled: false
                    },
                    toolbar: {
                        show: false
                    }
                },
                colors: colorsList,
                stroke: {
                    curve: 'smooth',
                    width: strokeWidthList
                },
                grid: {
                    borderColor: CHART_GRID_COLOR,
                    strokeDashArray: 3
                },
                xaxis: {
                    categories: [],
                    labels: {
                        style: {
                            colors: CHART_TICK_COLOR,
                            fontSize: '11px'
                        }
                    },
                    axisBorder: { show: false },
                    axisTicks: { show: false }
                },
                yaxis: {
                    min: 0,
                    max: 100,
                    labels: {
                        formatter: function(val) {
                            return Math.round(val) + '%';
                        },
                        style: {
                            colors: CHART_TICK_COLOR,
                            fontSize: '11px'
                        }
                    }
                },
                // Legend di samping (kanan) dengan fitur klik untuk show/hide individual core
                legend: {
                    show: isLegendVisible.value,
                    position: 'right',
                    horizontalAlign: 'center',
                    floating: false,
                    fontSize: '11px',
                    fontFamily: 'inherit',
                    fontWeight: 600,
                    offsetY: 0,
                    itemMargin: {
                        horizontal: 4,
                        vertical: 2
                    },
                    labels: {
                        colors: CHART_LEGEND_COLOR
                    },
                    onItemClick: {
                        toggleDataSeries: true
                    },
                    onItemHover: {
                        highlightDataSeries: true
                    }
                },
                tooltip: {
                    theme: 'light',
                    y: {
                        formatter: function(val) {
                            return val + '%';
                        }
                    }
                }
            };

            if (cpuApexChart) {
                cpuApexChart.destroy();
                cpuApexChart = null;
            }

            cpuApexChart = new ApexCharts(el, options);
            cpuApexChart.render();
        }

        // Inisialisasi Chart Single Metric Chart.js (RAM, Disk, Network)
        function createSingleLiveChart(canvasId, label, colorHex, bgColorRgba) {
            const ctx = document.getElementById(canvasId);
            if (!ctx) return null;

            return new Chart(ctx, {
                type: 'line',
                data: {
                    labels: [],
                    datasets: [{
                        label: label,
                        data: [],
                        borderColor: colorHex,
                        backgroundColor: bgColorRgba,
                        fill: true,
                        borderWidth: 2,
                        pointRadius: 0,
                        tension: 0.25
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    animation: false,
                    scales: {
                        x: {
                            grid: { color: CHART_GRID_COLOR },
                            ticks: { color: CHART_TICK_COLOR, maxRotation: 0 }
                        },
                        y: {
                            min: 0,
                            max: 100,
                            grid: { color: CHART_GRID_COLOR },
                            ticks: {
                                color: CHART_TICK_COLOR,
                                callback: function(val) { return val + '%'; }
                            }
                        }
                    },
                    plugins: {
                        legend: { display: false }
                    }
                }
            });
        }

        // Memperbarui data live chart setiap detik
        function updateLiveCharts(snapshot) {
            const now = new Date(snapshot.timestamp);
            const timeLabel = now.toTimeString().split(' ')[0];

            // 1. Update CPU Live ApexChart
            const currentCores = snapshot.cpuCores.length;
            if (!cpuApexChart) {
                initCpuApexChart(currentCores);
            }

            if (cpuApexChart) {
                // Tambahkan kategori waktu
                cpuTimeCategories.push(timeLabel);
                if (cpuTimeCategories.length > MAX_LIVE_POINTS) {
                    cpuTimeCategories.shift();
                }

                // Tambahkan data per core
                for (let i = 0; i < currentCores; i++) {
                    if (!cpuCoreSeriesData[i]) {
                        cpuCoreSeriesData[i] = [];
                    }
                    cpuCoreSeriesData[i].push(snapshot.cpuCores[i]);
                    if (cpuCoreSeriesData[i].length > MAX_LIVE_POINTS) {
                        cpuCoreSeriesData[i].shift();
                    }
                }

                // Tambahkan data Total CPU
                cpuTotalSeriesData.push(snapshot.cpuTotalPercent);
                if (cpuTotalSeriesData.length > MAX_LIVE_POINTS) {
                    cpuTotalSeriesData.shift();
                }

                // Bangun array series baru untuk ApexCharts
                const updatedSeries = [];
                for (let i = 0; i < currentCores; i++) {
                    updatedSeries.push({
                        name: 'Core ' + i,
                        data: cpuCoreSeriesData[i]
                    });
                }
                updatedSeries.push({
                    name: 'Total CPU',
                    data: cpuTotalSeriesData
                });

                // Update tanpa animasi agar halus setiap detik
                cpuApexChart.updateOptions({
                    xaxis: {
                        categories: cpuTimeCategories
                    }
                }, false, false);
                cpuApexChart.updateSeries(updatedSeries, false);

                // Pastikan garis yang di-uncheck oleh user tetap tersembunyi
                for (let i = 0; i < currentCores; i++) {
                    if (coreVisibility[i] === false) {
                        cpuApexChart.hideSeries('Core ' + i);
                    }
                }
                if (totalVisibility.value === false) {
                    cpuApexChart.hideSeries('Total CPU');
                }
            }

            // 2. Helper update single chart
            function pushSingleMetric(chart, value) {
                if (!chart) return;
                chart.data.labels.push(timeLabel);
                chart.data.datasets[0].data.push(value);

                if (chart.data.labels.length > MAX_LIVE_POINTS) {
                    chart.data.labels.shift();
                    chart.data.datasets[0].data.shift();
                }
                chart.update('none');
            }

            // Live Disk (kini di samping CPU di baris atas)
            pushSingleMetric(diskLiveChart, snapshot.diskUsagePercent);
            // Live RAM (kini di baris bawah)
            pushSingleMetric(ramLiveChart, snapshot.ramUsagePercent);
            // Live Network (di baris bawah)
            pushSingleMetric(netLiveChart, currentSelectedNetworkUsage.value);

            // Live IIS per Website (CPU dan Memory)
            updateIisLiveCharts(snapshot.iisWebsites, timeLabel);
        }

        // Inisialisasi Chart Live IIS Multi-Series Chart.js
        function createIisLiveChart(canvasId, yAxisFormatter, maxVal) {
            const ctx = document.getElementById(canvasId);
            if (!ctx) {
                return null;
            }

            const yAxisConfig = {
                min: 0,
                grid: { color: CHART_GRID_COLOR },
                ticks: {
                    color: CHART_TICK_COLOR,
                    callback: function(val) {
                        return val + yAxisFormatter;
                    }
                }
            };

            if (maxVal !== null && maxVal !== undefined) {
                yAxisConfig.max = maxVal;
            }

            return new Chart(ctx, {
                type: 'line',
                data: {
                    labels: [],
                    datasets: []
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    animation: false,
                    scales: {
                        x: {
                            grid: { color: CHART_GRID_COLOR },
                            ticks: { color: CHART_TICK_COLOR, maxRotation: 0 }
                        },
                        y: yAxisConfig
                    },
                    plugins: {
                        legend: {
                            display: true,
                            position: 'top',
                            align: 'start',
                            labels: {
                                boxWidth: 10,
                                boxHeight: 10,
                                usePointStyle: true,
                                pointStyle: 'circle',
                                color: CHART_LEGEND_COLOR,
                                font: { size: 10, weight: '600' },
                                padding: 6
                            }
                        },
                        tooltip: {
                            mode: 'index',
                            intersect: false
                        }
                    }
                }
            });
        }

        // Memperbarui grafik live IIS CPU dan Memory per website
        function updateIisLiveCharts(websites, timeLabel) {
            if (!iisCpuLiveChart || !iisMemLiveChart) {
                return;
            }

            // Tambahkan waktu saat ini ke riwayat label waktu IIS
            iisTimeCategories.push(timeLabel);
            if (iisTimeCategories.length > IIS_MAX_LIVE_POINTS) {
                iisTimeCategories.shift();
            }

            const activeWebsites = websites || [];
            const siteMap = {};

            for (let i = 0; i < activeWebsites.length; i++) {
                const site = activeWebsites[i];
                siteMap[site.websiteName] = site;

                if (!iisCpuSeriesData[site.websiteName]) {
                    iisCpuSeriesData[site.websiteName] = [];
                    iisMemSeriesData[site.websiteName] = [];
                    // Isi data awal dengan 0 agar sejajar dengan label waktu
                    for (let k = 0; k < iisTimeCategories.length - 1; k++) {
                        iisCpuSeriesData[site.websiteName].push(0);
                        iisMemSeriesData[site.websiteName].push(0);
                    }
                }
            }

            const allSiteNames = Object.keys(iisCpuSeriesData);

            for (let i = 0; i < allSiteNames.length; i++) {
                const sName = allSiteNames[i];
                let currentCpu = 0;
                let currentMem = 0;

                if (siteMap[sName]) {
                    currentCpu = siteMap[sName].cpuPercent;
                    currentMem = siteMap[sName].memoryMb;
                }

                iisCpuSeriesData[sName].push(currentCpu);
                iisMemSeriesData[sName].push(currentMem);

                if (iisCpuSeriesData[sName].length > IIS_MAX_LIVE_POINTS) {
                    iisCpuSeriesData[sName].shift();
                }
                if (iisMemSeriesData[sName].length > IIS_MAX_LIVE_POINTS) {
                    iisMemSeriesData[sName].shift();
                }
            }

            // Bangun dataset Chart.js untuk CPU dan Memory
            const cpuDatasets = [];
            const memDatasets = [];

            for (let i = 0; i < allSiteNames.length; i++) {
                const sName = allSiteNames[i];
                const color = IIS_PALETTE[i % IIS_PALETTE.length];

                cpuDatasets.push({
                    label: sName,
                    data: iisCpuSeriesData[sName],
                    borderColor: color,
                    backgroundColor: color,
                    fill: false,
                    borderWidth: 2,
                    pointRadius: 0,
                    tension: 0.25
                });

                memDatasets.push({
                    label: sName,
                    data: iisMemSeriesData[sName],
                    borderColor: color,
                    backgroundColor: color,
                    fill: false,
                    borderWidth: 2,
                    pointRadius: 0,
                    tension: 0.25
                });
            }

            iisCpuLiveChart.data.labels = iisTimeCategories.slice();
            iisCpuLiveChart.data.datasets = cpuDatasets;
            iisCpuLiveChart.update('none');

            iisMemLiveChart.data.labels = iisTimeCategories.slice();
            iisMemLiveChart.data.datasets = memDatasets;
            iisMemLiveChart.update('none');
        }

        // Mengambil histori dari API SQLite
        let isFetchingHistory = false;
        async function fetchHistory() {
            if (isFetchingHistory) {
                return;
            }
            isFetchingHistory = true;
            isLoadingHistory.value = true;

            // Prioritaskan pengambilan via WebSocket SignalR yang stabil
            if (signalRConnection && connectionStatus.value === 'connected') {
                try {
                    const data = await signalRConnection.invoke('RequestHistory', selectedRange.value);
                    if (data) {
                        historyRecords.value = data;
                        renderHistoryChart();
                        isFetchingHistory = false;
                        isLoadingHistory.value = false;
                        return;
                    }
                } catch (wsErr) {
                    console.warn('Fallback ke fetch HTTP untuk histori:', wsErr);
                }
            }

            // Jalur sekunder: Fetch via HTTP endpoint
            try {
                const url = '/api/metrics/history?range=' + encodeURIComponent(selectedRange.value);
                const response = await fetch(url);
                if (response.ok) {
                    const data = await response.json();
                    historyRecords.value = data;
                    renderHistoryChart();
                }
            } catch (err) {
                console.error('Gagal mengambil histori metrik:', err);
            } finally {
                isFetchingHistory = false;
                isLoadingHistory.value = false;
            }
        }

        // Render line chart histori sesuai metrik yang dipilih
        function renderHistoryChart() {
            const ctx = document.getElementById('historyCanvas');
            if (!ctx) return;

            const records = historyRecords.value;
            const labels = [];
            const datasets = [];

            // Siapkan label waktu histori
            for (let i = 0; i < records.length; i++) {
                const dt = new Date(records[i].timestamp);
                let formattedTime = '';

                if (selectedRange.value === '1h' || selectedRange.value === '6h') {
                    formattedTime = dt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
                } else {
                    const datePart = (dt.getMonth() + 1) + '/' + dt.getDate();
                    const timePart = dt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
                    formattedTime = datePart + ' ' + timePart;
                }
                labels.push(formattedTime);
            }

            // Tentukan datasets berdasarkan opsi metrik yang dipilih
            if (selectedMetric.value === 'cpu') {
                let coreCount = systemInfo.processorCount;
                if (!coreCount || coreCount <= 0) {
                    coreCount = 12;
                }

                for (let c = 0; c < coreCount; c++) {
                    const coreData = [];
                    for (let r = 0; r < records.length; r++) {
                        if (records[r].cpuCores && records[r].cpuCores[c] !== undefined) {
                            coreData.push(records[r].cpuCores[c]);
                        } else {
                            coreData.push(0);
                        }
                    }
                    datasets.push({
                        label: 'Core ' + c,
                        data: coreData,
                        hidden: !historyCoreVisibility[c],
                        borderColor: generateCoreColor(c, coreCount),
                        backgroundColor: 'transparent',
                        borderWidth: 1.5,
                        pointRadius: 1,
                        tension: 0.1
                    });
                }

                const totalCpuData = [];
                for (let r = 0; r < records.length; r++) {
                    totalCpuData.push(records[r].cpuTotal);
                }
                datasets.push({
                    label: 'Total CPU',
                    data: totalCpuData,
                    hidden: !historyTotalVisibility.value,
                    borderColor: '#0f172a',
                    backgroundColor: 'transparent',
                    borderWidth: 3,
                    pointRadius: 2,
                    tension: 0.1,
                    order: -1
                });
            } else if (selectedMetric.value === 'ram') {
                const ramData = [];
                for (let r = 0; r < records.length; r++) {
                    ramData.push(records[r].ramUsagePercent);
                }
                datasets.push({
                    label: 'RAM Usage (%)',
                    data: ramData,
                    borderColor: '#7c3aed',
                    backgroundColor: 'rgba(124, 58, 237, 0.1)',
                    fill: true,
                    borderWidth: 2,
                    pointRadius: 2,
                    tension: 0.1
                });
            } else if (selectedMetric.value === 'disk') {
                const diskData = [];
                for (let r = 0; r < records.length; r++) {
                    diskData.push(records[r].diskUsagePercent);
                }
                datasets.push({
                    label: 'Disk D Active Time (%)',
                    data: diskData,
                    borderColor: '#d97706',
                    backgroundColor: 'rgba(217, 119, 6, 0.1)',
                    fill: true,
                    borderWidth: 2,
                    pointRadius: 2,
                    tension: 0.1
                });
            } else if (selectedMetric.value === 'network') {
                const netData = [];
                for (let r = 0; r < records.length; r++) {
                    netData.push(records[r].networkUsagePercent);
                }
                datasets.push({
                    label: 'Total Network Usage (%)',
                    data: netData,
                    borderColor: '#059669',
                    backgroundColor: 'rgba(5, 150, 105, 0.1)',
                    fill: true,
                    borderWidth: 2,
                    pointRadius: 2,
                    tension: 0.1
                });
            } else if (selectedMetric.value === 'all') {
                const cpuArr = [];
                const ramArr = [];
                const diskArr = [];
                const netArr = [];
                for (let r = 0; r < records.length; r++) {
                    cpuArr.push(records[r].cpuTotal);
                    ramArr.push(records[r].ramUsagePercent);
                    diskArr.push(records[r].diskUsagePercent);
                    netArr.push(records[r].networkUsagePercent);
                }
                datasets.push({
                    label: 'CPU Total (%)',
                    data: cpuArr,
                    hidden: !historyAllVisibility.cpu,
                    borderColor: '#0284c7',
                    borderWidth: 2,
                    pointRadius: 1,
                    tension: 0.1
                });
                datasets.push({
                    label: 'RAM (%)',
                    data: ramArr,
                    hidden: !historyAllVisibility.ram,
                    borderColor: '#7c3aed',
                    borderWidth: 2,
                    pointRadius: 1,
                    tension: 0.1
                });
                datasets.push({
                    label: 'Disk D (%)',
                    data: diskArr,
                    hidden: !historyAllVisibility.disk,
                    borderColor: '#d97706',
                    borderWidth: 2,
                    pointRadius: 1,
                    tension: 0.1
                });
                datasets.push({
                    label: 'Total Network (%)',
                    data: netArr,
                    hidden: !historyAllVisibility.network,
                    borderColor: '#059669',
                    borderWidth: 2,
                    pointRadius: 1,
                    tension: 0.1
                });
            }

            if (historyChart) {
                historyChart.destroy();
                historyChart = null;
            }

            historyChart = new Chart(ctx, {
                type: 'line',
                data: {
                    labels: labels,
                    datasets: datasets
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    interaction: {
                        mode: 'index',
                        intersect: false
                    },
                    scales: {
                        x: {
                            grid: { color: CHART_GRID_COLOR },
                            ticks: { color: CHART_TICK_COLOR }
                        },
                        y: {
                            min: 0,
                            max: 100,
                            grid: { color: CHART_GRID_COLOR },
                            ticks: {
                                color: CHART_TICK_COLOR,
                                callback: function(val) { return val + '%'; }
                            }
                        }
                    },
                    plugins: {
                        legend: {
                            display: false
                        }
                    }
                }
            });
        }

        // Toggle visibilitas core CPU pada grafik histori
        function toggleHistoryCore(idx) {
            if (historyCoreVisibility[idx] === undefined) {
                historyCoreVisibility[idx] = false;
            }
            historyCoreVisibility[idx] = !historyCoreVisibility[idx];
            if (historyChart) {
                historyChart.setDatasetVisibility(idx, historyCoreVisibility[idx]);
                historyChart.update();
            }
        }

        // Toggle visibilitas Total CPU pada grafik histori
        function toggleHistoryTotal() {
            historyTotalVisibility.value = !historyTotalVisibility.value;
            if (historyChart) {
                let coreCount = systemInfo.processorCount;
                if (!coreCount || coreCount <= 0) {
                    coreCount = 12;
                }
                historyChart.setDatasetVisibility(coreCount, historyTotalVisibility.value);
                historyChart.update();
            }
        }

        // Toggle visibilitas metrik pada mode Semua Metrik Gabungan
        function toggleHistoryAll(key) {
            if (historyAllVisibility[key] !== undefined) {
                historyAllVisibility[key] = !historyAllVisibility[key];
            }
            if (historyChart) {
                let datasetIndex = 0;
                if (key === 'cpu') {
                    datasetIndex = 0;
                } else if (key === 'ram') {
                    datasetIndex = 1;
                } else if (key === 'disk') {
                    datasetIndex = 2;
                } else if (key === 'network') {
                    datasetIndex = 3;
                }
                historyChart.setDatasetVisibility(datasetIndex, historyAllVisibility[key]);
                historyChart.update();
            }
        }

        // Pilihan rentang histori
        function setRange(range) {
            selectedRange.value = range;
            fetchHistory();
        }

        // Pilihan filter metrik histori
        function setMetric(metric) {
            selectedMetric.value = metric;
            renderHistoryChart();
        }

        // Inisialisasi koneksi SignalR
        function startSignalR() {
            const connection = new signalR.HubConnectionBuilder()
                .withUrl('/hubs/metrics')
                .withAutomaticReconnect()
                .build();

            signalRConnection = connection;

            connection.onreconnecting(function() {
                connectionStatus.value = 'reconnecting';
                connectionText.value = 'Menghubungkan Ulang...';
            });

            connection.onreconnected(function() {
                connectionStatus.value = 'connected';
                connectionText.value = 'Terhubung';
                fetchHistory();
                fetchTopProcesses();
            });

            connection.onclose(function() {
                connectionStatus.value = 'disconnected';
                connectionText.value = 'Terputus';
                setTimeout(startConnection, 3000);
            });

            // Menerima snapshot live tiap 1 detik dari server
            connection.on('ReceiveLiveMetrics', function(snapshot) {
                live.timestamp = new Date(snapshot.timestamp).toLocaleTimeString();
                live.cpuTotalPercent = snapshot.cpuTotalPercent;
                live.cpuCores = snapshot.cpuCores;
                live.coreCount = snapshot.coreCount;
                live.ramUsagePercent = snapshot.ramUsagePercent;
                live.ramTotalBytes = snapshot.ramTotalBytes;
                live.ramUsedBytes = snapshot.ramUsedBytes;
                live.ramAvailableBytes = snapshot.ramAvailableBytes;
                live.diskUsagePercent = snapshot.diskUsagePercent;
                live.appDiskName = snapshot.appDiskName || 'Drive D:';
                live.disks = snapshot.disks || [];
                live.networkUsagePercent = snapshot.networkUsagePercent;
                live.networkBytesPerSec = snapshot.networkBytesPerSec;
                live.networkAdapters = snapshot.networkAdapters || [];
                live.iisWebsites = snapshot.iisWebsites || [];

                // Perbarui live charts
                updateLiveCharts(snapshot);
            });

            // Menerima Top 20 Program CPU & RAM secara real-time via WebSocket
            connection.on('ReceiveTopProcesses', function(data) {
                if (data && data.topCpu && data.topRam) {
                    topProcesses.topCpu = data.topCpu;
                    topProcesses.topRam = data.topRam;
                    isLoadingTopProcesses.value = false;
                }
            });

            function startConnection() {
                connection.start().then(function() {
                    connectionStatus.value = 'connected';
                    connectionText.value = 'Terhubung (Live)';
                    fetchHistory();
                    fetchTopProcesses();
                }).catch(function(err) {
                    connectionStatus.value = 'disconnected';
                    connectionText.value = 'Gagal Konek';
                    console.error('SignalR start error:', err);
                    setTimeout(startConnection, 3000);
                });
            }

            startConnection();
        }

        // Mengambil spesifikasi info sistem secara cepat dan non-blocking
        function fetchSystemInfo() {
            fetch('/api/metrics/info')
                .then(function(res) {
                    if (res.ok) {
                        return res.json();
                    }
                    return null;
                })
                .then(function(data) {
                    if (data) {
                        systemInfo.machineName = data.machineName;
                        systemInfo.osDescription = data.osDescription;
                        systemInfo.processorCount = data.processorCount;
                        systemInfo.totalMemoryBytes = data.totalMemoryBytes;

                        if (data.cpuHighThresholdPercent > 0) {
                            systemInfo.cpuHighThresholdPercent = data.cpuHighThresholdPercent;
                        }

                        if (!cpuApexChart) {
                            initCpuApexChart(data.processorCount);
                        }
                    }
                })
                .catch(function(err) {
                    console.error('Peringatan fetch info sistem:', err);
                });
        }

        onMounted(function() {
            // 1. Inisialisasi Disk chart di baris atas samping CPU
            diskLiveChart = createSingleLiveChart('diskLiveCanvas', 'Disk %', '#d97706', 'rgba(217, 119, 6, 0.1)');

            // 2. Inisialisasi RAM chart di baris bawah
            ramLiveChart = createSingleLiveChart('ramLiveCanvas', 'RAM %', '#7c3aed', 'rgba(124, 58, 237, 0.1)');

            // 3. Inisialisasi Network chart di baris bawah
            netLiveChart = createSingleLiveChart('netLiveCanvas', 'Network %', '#059669', 'rgba(5, 150, 105, 0.1)');

            // 3b. Inisialisasi Chart Live IIS CPU & Memory per website
            iisCpuLiveChart = createIisLiveChart('iisCpuLiveCanvas', '%', 100);
            iisMemLiveChart = createIisLiveChart('iisMemLiveCanvas', ' MB', null);

            // 4. Inisialisasi CPU ApexChart Line dengan legend di samping
            initCpuApexChart(12);

            // 5. Ambil spesifikasi sistem secara non-blocking
            fetchSystemInfo();

            // 6. Mulai streaming SignalR
            startSignalR();

            // 7. Muat histori dari SQLite dengan jeda awal 300ms agar koneksi HTTP tidak bertabrakan
            setTimeout(function() {
                fetchHistory();
            }, 300);

            setInterval(function() {
                fetchHistory();
            }, 60000);

            // 8. Ambil data top 20 program saat web pertama kali dimuat
            fetchTopProcesses();

            // 9. Refresh data top 20 program tiap 10 detik secara otomatis
            setInterval(function() {
                fetchTopProcesses();
            }, 10000);

            // 10. Muat log proses CPU tinggi saat halaman pertama kali dibuka
            fetchCpuHighLogs(1);

            // 11. Muat statistik process CPU tinggi saat halaman pertama kali dibuka
            fetchCpuHighStats();

            // 12. Refresh log proses CPU tinggi tiap 30 detik
            setInterval(function() {
                fetchCpuHighLogs();
            }, 30000);

            // 13. Refresh statistik process CPU tinggi tiap 30 detik
            setInterval(function() {
                fetchCpuHighStats();
            }, 30000);
        });

        return {
            coreVisibility,
            totalVisibility,
            toggleCoreSeries,
            toggleTotalSeries,
            selectedNetworkDisplayName,
            connectionStatus,
            connectionText,
            isLegendVisible,
            toggleLegend,
            systemInfo,
            live,
            selectedNetworkName,
            currentSelectedNetworkUsage,
            currentSelectedNetworkSpeed,
            displayMachineName,
            displayOsDescription,
            displayCoreCount,
            displayTotalRam,
            selectedRange,
            selectedMetric,
            isLoadingHistory,
            historyRecords,
            formatBytes,
            formatNetworkSpeed,
            generateCoreColor,
            setRange,
            setMetric,
            fetchHistory,
            topProcesses,
            isLoadingTopProcesses,
            fetchTopProcesses,
            cpuHighLogs,
            isLoadingCpuHighLogs,
            fetchCpuHighLogs,
            goToCpuHighLogPage,
            cpuHighLogFilter,
            setCpuHighLogFilter,
            cpuHighLogPage,
            cpuHighLogPageNumbers,
            cpuHighLogTotal,
            cpuHighLogTotalPages,
            cpuHighStats,
            fetchCpuHighStats,
            formatLogDateTime,
            getCpuBadgeClass,
            isCpuDropLog,
            getCpuLogBadgeClass,
            getCpuLogTrendIcon,
            getCpuLogTrendTitle,
            formatMb,
            historyCoreVisibility,
            historyTotalVisibility,
            historyAllVisibility,
            toggleHistoryCore,
            toggleHistoryTotal,
            toggleHistoryAll
        };
    }
}).mount('#app');
