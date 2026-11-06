using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Security.Principal;

namespace RiotSwitcher // ĐỔI LẠI THÀNH NAMESPACE CỦA BẠN NẾU KHÁC
{
    public enum AppState
    {
        Idle,       // Rảnh rỗi (cho phép timer quét)
        LoggingIn   // Đang kẹt xử lý login (khóa timer)
    }
    public class AccountModel
    {
        public string DisplayName { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        
        [JsonIgnore]
        public string Rank { get; set; } = "Unranked";
        [JsonIgnore]
        public string RankColor { get; set; } = "#A6ADC8";

    }

    public class RankCacheItem
    {
        public string Rank { get; set; }
        public DateTime ExpireTime { get; set; }
    }



    public partial class MainWindow : System.Windows.Window
    {
        private string _riotClientPath = "";
        private const string SettingsFile = "setting.ini";
        private readonly string _lockfilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Riot Games\Riot Client\Config\lockfile");
        private readonly string _dbFilePath = "accounts.json";
        private const string RankCacheFile = "rank_cache.json";
        private Dictionary<string, RankCacheItem> _rankCache = new Dictionary<string, RankCacheItem>();
        private AppState _currentState = AppState.Idle;

        private List<AccountModel> _accounts = new();
        private bool _isEditing = false;
        private DispatcherTimer _processTimer;

        public MainWindow()
        {
            if (!IsAdministrator())
            {
                MessageBox.Show("Vui lòng click chuột phải vào Tool -> Chọn 'Run as administrator' để có thể điều khiển được Riot Client!",
                                "Thiếu quyền", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown();
                return;
            }

            InitializeComponent();
            Task.Delay(1000);
            InitializeSettings();
            LoadAccounts();
            LoadAllRanksAsync();
            InitProcessCheckTimer();
        }

        private bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void TopBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                DragMove();
            }
        }

        // Nút Thu Nhỏ
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        // Nút Tắt App
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            // Có thể dùng Application.Current.Shutdown(); nếu muốn diệt tận gốc mọi luồng ngầm
            Close();
        }

        private void InitProcessCheckTimer()
        {
            _processTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _processTimer.Tick += (s, e) =>
            {
                if (_currentState != AppState.Idle) return;
                if (Process.GetProcessesByName("VALORANT").Length > 0) TxtStatus.Text = "Valorant is running";
                else if (Process.GetProcessesByName("RiotClientServices").Length > 0) TxtStatus.Text = "Riot Client is running";
                else TxtStatus.Text = "Waiting Riot Client...";
            };
            _processTimer.Start();
        }

        private void debugLog(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[DEBUG] {message}");
        }

        

        private void InitializeSettings()
        {
            // 1. KIỂM TRA FILE SETTING.INI ĐÃ CÓ CHƯA
            if (File.Exists(SettingsFile))
            {
                var lines = File.ReadAllLines(SettingsFile);
                foreach (var line in lines)
                {
                    if (line.StartsWith("RiotClientPath="))
                    {
                        string path = line.Substring("RiotClientPath=".Length).Trim();
                        // Nếu đường dẫn trong file có thật, lấy luôn và thoát hàm
                        if (File.Exists(path))
                        {
                            _riotClientPath = path;
                            return;
                        }
                    }
                }
            }

            // 2. NẾU CHƯA CÓ FILE (Lần chạy đầu) -> QUÉT PROCESS XEM RIOT CÓ ĐANG MỞ KHÔNG
            _riotClientPath = GetPathFromRunningProcess();

            // 3. NẾU PROCESS KHÔNG CHẠY -> MỞ DIALOG BẮT NGƯỜI DÙNG CHỌN
            if (string.IsNullOrEmpty(_riotClientPath) || !File.Exists(_riotClientPath))
            {
                MessageBox.Show("Đây là lần chạy đầu tiên!\nTool không tìm thấy Riot Client đang mở.\n\nVui lòng chọn đường dẫn tới file RiotClientServices.exe của bạn.",
                                "Thiết lập ban đầu", MessageBoxButton.OK, MessageBoxImage.Information);

                OpenFileDialog openFileDialog = new OpenFileDialog();
                openFileDialog.Filter = "Riot Client (RiotClientServices.exe)|RiotClientServices.exe";
                openFileDialog.Title = "Chọn đường dẫn Riot Client";

                if (openFileDialog.ShowDialog() == true)
                {
                    _riotClientPath = openFileDialog.FileName;
                }
                else
                {
                    // Nếu người dùng bấm Cancel, không cho dùng Tool nữa
                    MessageBox.Show("Bạn bắt buộc phải chọn đường dẫn để sử dụng tool. Ứng dụng sẽ thoát.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                    System.Windows.Application.Current.Shutdown();
                    return;
                }
            }

            // 4. TẠO HOẶC GHI ĐÈ FILE SETTING.INI LƯU LẠI CHO LẦN SAU
            File.WriteAllText(SettingsFile, $"RiotClientPath={_riotClientPath}");
        }

        // Hàm hỗ trợ: Trích xuất đường dẫn từ Process đang chạy
        private string GetPathFromRunningProcess()
        {
            try
            {
                // Tìm tất cả các tiến trình có tên "RiotClientServices"
                var processes = Process.GetProcessesByName("RiotClientServices");
                if (processes.Length > 0)
                {
                    // Lấy đường dẫn của tiến trình đầu tiên tìm thấy
                    return processes[0].MainModule.FileName;
                }
            }
            catch (Exception ex)
            {
                // Bỏ qua lỗi nếu Tool không có quyền (Admin) để đọc Process của Windows
                debugLog($"Không thể lấy đường dẫn từ process: {ex.Message}");
            }
            return null;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // Kiểm tra xem cửa sổ có đang bị Minimize không
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        // Hàm hiển thị / Restore cửa sổ
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        private void LoadAccounts()
        {
            if (File.Exists(_dbFilePath))
            {
                try
                {
                    _accounts = JsonSerializer.Deserialize<List<AccountModel>>(File.ReadAllText(_dbFilePath)) ?? new();
                }
                catch { _accounts = new(); }
            }
            RefreshAccountList();
        }

        private async Task LoadAllRanksAsync()
        {
            LoadRankCache();
            // Duyệt qua từng tài khoản trong danh sách
            foreach (var acc in _accounts)
            {
                // Bỏ qua nếu acc chưa có Tên In-game (ví dụ user mới thêm chưa login lần nào)
                if (!acc.DisplayName.Contains("#"))
                {
                    acc.Rank = "Unranked";
                    acc.RankColor = "#585B70"; // Unranked (Xám tối)
                    continue;
                }

                await Task.Delay(400);
                string currentRank = await GetRankFromTrackerAsync(acc.DisplayName);

                acc.Rank = currentRank;

                // Tùy biến màu theo Rank
                if (currentRank.Contains("Iron") || currentRank.Contains("Bronze") || currentRank.Contains("Silver") ) 
                    acc.RankColor = "#A6ADC8"; // Màu Xám
                else if (currentRank.Contains("Gold"))
                    acc.RankColor = "#F9E2AF"; // Màu Vàng
                else if (currentRank.Contains("Platinum"))
                    acc.RankColor = "#89B4FA"; // Màu Xanh dương
                else if (currentRank.Contains("Diamond"))
                    acc.RankColor = "#CBA6F7"; // Màu Tím
                else if (currentRank.Contains("Ascendant"))
                    acc.RankColor = "#A6E3A1"; // Màu Xanh lá
                else if (currentRank.Contains("Immortal"))
                    acc.RankColor = "#F38BA8"; // Màu Đỏ
                else if (currentRank.Contains("Radiant"))
                    acc.RankColor = "#FAB387"; // Màu Cam rực cháy
                else
                    acc.RankColor = "#585B70"; // Mặc định Xám nếu không xác định được Rank

                // Ép giao diện F5 lại để hiện Rank mới lên 
            }
            LstAccounts.Items.Refresh();
        }

        private void LoadRankCache()
        {
            if (File.Exists(RankCacheFile))
            {
                try
                {
                    string json = File.ReadAllText(RankCacheFile);
                    _rankCache = JsonSerializer.Deserialize<Dictionary<string, RankCacheItem>>(json) ?? new Dictionary<string, RankCacheItem>();
                }
                catch { _rankCache = new Dictionary<string, RankCacheItem>(); }
            }
        }

        private void SaveRankCache()
        {
            try
            {
                string json = JsonSerializer.Serialize(_rankCache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(RankCacheFile, json);
            }
            catch { }
        }

        private async Task<string> GetRankFromTrackerAsync(string riotId)
        {
            // 1. KIỂM TRA BỘ NHỚ ĐỆM (CACHE) 30 PHÚT
            if (_rankCache.TryGetValue(riotId, out var cachedData))
            {
                if (DateTime.Now < cachedData.ExpireTime)
                {
                    return cachedData.Rank;
                }
            }

            string[] parts = riotId.Split('#');
            if (parts.Length != 2) return "Lỗi Tên";
            string name = parts[0];
            string tag = parts[1];
            string finalRank = "Unranked";
            await Task.Delay(1000); // Delay 1.0 giây để tránh spam API quá nhanh
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "RiotAccountSwitcher_Project");

            // 2. LỚP 1: GỌI KYROSKOH (Nhanh, không cần Key)
            try
            {
                string urlKyros = $"https://api.kyroskoh.xyz/valorant/v1/mmr/ap/{name}/{tag}?show=rankonly&display=0";
                var resKyros = await client.GetAsync(urlKyros);

                if (resKyros.IsSuccessStatusCode)
                {
                    string resultText = await resKyros.Content.ReadAsStringAsync();
                    string cleanRank = resultText.Replace(".", "").Trim();

                    if (!cleanRank.Contains("API Error") && !cleanRank.ToLower().Contains("error") && !string.IsNullOrEmpty(cleanRank))
                    {
                        finalRank = cleanRank;
                        goto SaveAndReturn; // Nếu KyrosKoh ngon lành, nhảy thẳng xuống lưu Cache
                    }
                }
            }
            catch { finalRank = "Busy"; }

            //// 3. LỚP 2: FALLBACK VỀ HENRIKDEV NẾU KYROSKOH SẬP
            //try
            //{
            //    string urlHenrik = $"https://api.henrikdev.xyz/valorant/v1/mmr/ap/{name}/{tag}";

            //    // Ép thêm Header API Key cho HenrikDev
            //    // CHÚ Ý: Điền API Key của bác vào đây
            //    client.DefaultRequestHeaders.Add("Authorization", "HDEV-NHAP_API_KEY_CUA_BAC_VAO_DAY");

            //    var resHenrik = await client.GetAsync(urlHenrik);
            //    if (resHenrik.IsSuccessStatusCode)
            //    {
            //        string json = await resHenrik.Content.ReadAsStringAsync();
            //        using JsonDocument doc = JsonDocument.Parse(json);

            //        string rankValue = doc.RootElement.GetProperty("data").GetProperty("currenttierpatched").GetString();
            //        finalRank = string.IsNullOrEmpty(rankValue) ? "Unranked" : rankValue;
            //    }
            //    else
            //    {
            //        finalRank = $"Lỗi API ({resHenrik.StatusCode})";
            //    }
            //}
            //catch { finalRank = "Máy chủ bận"; }

            SaveAndReturn:
            // 4. LƯU XUỐNG KHO (CACHE 30 PHÚT) RỒI TRẢ VỀ CHO GIAO DIỆN
            if (finalRank == "Unranked" || finalRank == "Busy" || finalRank.StartsWith("Lỗi") || finalRank.Contains("API Error"))
            {
                // Nếu là Unranked hoặc lỗi thì không lưu vào cache, để lần sau vẫn thử lại
            }
            else
            {
                _rankCache[riotId] = new RankCacheItem
                {
                    Rank = finalRank,
                    ExpireTime = DateTime.Now.AddHours(1)
                };
                SaveRankCache();
             }
            return finalRank;
        }

        public static class CryptoHelper
        {
            // Chìa khóa bí mật dùng để XOR (bạn có thể đổi thành tên bạn cho ngầu)
            private const string SecretKey = "riotShuAkashi";

            public static string Encrypt(string plainText)
            {
                if (string.IsNullOrEmpty(plainText)) return string.Empty;

                // Bước 1: Mã hóa XOR từng ký tự với khóa bí mật
                char[] xorResult = new char[plainText.Length];
                for (int i = 0; i < plainText.Length; i++)
                {
                    xorResult[i] = (char)(plainText[i] ^ SecretKey[i % SecretKey.Length]);
                }

                // Bước 2: Đảo ngược thứ tự chuỗi để làm rối thêm
                Array.Reverse(xorResult);

                // Bước 3: Bọc lại bằng Base64 cho đẹp file JSON
                byte[] bytes = Encoding.UTF8.GetBytes(new string(xorResult));
                return Convert.ToBase64String(bytes);
            }

            public static string Decrypt(string encodedText)
            {
                if (string.IsNullOrEmpty(encodedText)) return string.Empty;
                try
                {
                    // Bước 1: Lột lớp bọc Base64
                    byte[] bytes = Convert.FromBase64String(encodedText);
                    char[] chars = Encoding.UTF8.GetString(bytes).ToCharArray();

                    // Bước 2: Đảo ngược chuỗi về lại chiều thuận
                    Array.Reverse(chars);

                    // Bước 3: Giải mã XOR (phép XOR có tính chất đảo nghịch, XOR lần 2 sẽ ra bản gốc)
                    char[] original = new char[chars.Length];
                    for (int i = 0; i < chars.Length; i++)
                    {
                        original[i] = (char)(chars[i] ^ SecretKey[i % SecretKey.Length]);
                    }

                    return new string(original);
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        private void SaveAccountsToFile() => File.WriteAllText(_dbFilePath, JsonSerializer.Serialize(_accounts, new JsonSerializerOptions { WriteIndented = true }));

        private void RefreshAccountList()
        {
            LstAccounts.ItemsSource = null;
            LstAccounts.ItemsSource = _accounts;
            ClearForm();
        }

        private void ClearForm()
        {
            TxtDisplayName.Text = TxtUsername.Text = TxtPassword.Password = "";
            TxtDisplayName.IsEnabled = true;
            _isEditing = false;
            LblFormTitle.Text = "Thêm tài khoản mới";
            BtnSelect.IsEnabled = BtnEdit.IsEnabled = BtnDelete.IsEnabled = false;
        }

        private void LstAccounts_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            BtnSelect.IsEnabled = BtnEdit.IsEnabled = BtnDelete.IsEnabled = LstAccounts.SelectedItem != null;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtUsername.Text) || string.IsNullOrWhiteSpace(TxtPassword.Password))
                return;

            if (_isEditing)
            {
                // Lúc Edit, lấy thẳng account đang được chọn trên List để sửa cho an toàn
                if (LstAccounts.SelectedItem is AccountModel acc)
                {
                    acc.Username = TxtUsername.Text;
                    acc.Password = CryptoHelper.Encrypt(TxtPassword.Password);
                    // DisplayName giữ nguyên, không đổi
                }
            }
            else
            {
                // Lấy tạm Username làm DisplayName lúc mới tạo
                string tempDisplayName = TxtUsername.Text;

                // Check trùng
                if (_accounts.Any(a => a.DisplayName.Equals(tempDisplayName, StringComparison.OrdinalIgnoreCase)))
                    return;

                _accounts.Add(new AccountModel
                {
                    DisplayName = tempDisplayName,
                    Username = TxtUsername.Text,
                    Password = CryptoHelper.Encrypt(TxtPassword.Password)
                });
            }

            SaveAccountsToFile();
            RefreshAccountList();       
        }

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            if (LstAccounts.SelectedItem is AccountModel acc)
            {
                TxtDisplayName.Text = acc.DisplayName;
                TxtDisplayName.IsEnabled = false;
                TxtUsername.Text = acc.Username;
                TxtPassword.Password = CryptoHelper.Decrypt(acc.Password);
                _isEditing = true;
                LblFormTitle.Text = "Cập nhật tài khoản";
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (LstAccounts.SelectedItem is AccountModel acc && MessageBox.Show($"Xóa {acc.DisplayName}?", "Xác nhận", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _accounts.Remove(acc);
                SaveAccountsToFile();
                RefreshAccountList();
            }
        }

        private async Task<bool> RiotApiLogoutAsync()
        {
            if (!File.Exists(_lockfilePath)) return false;
            try
            {
                // Sửa lỗi IOException: Đọc ké lockfile bằng FileShare.ReadWrite
                using var fs = new FileStream(_lockfilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string lockContent = await sr.ReadToEndAsync();

                string[] parts = lockContent.Split(':');
                if (parts.Length < 4) return false;

                using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = delegate { return true; } });
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{parts[3]}")));

                var res = await client.DeleteAsync($"https://127.0.0.1:{parts[2]}/rso-auth/v1/session");
                return res.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private async Task<string> GetCurrentRiotIdAsync()
        {
            if (!File.Exists(_lockfilePath)) return "Chưa mở Riot Client";
            try
            {
                // Vẫn bài cũ: Đọc ké lockfile để lấy Port và Password
                using var fs = new FileStream(_lockfilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string lockContent = await sr.ReadToEndAsync();

                string[] parts = lockContent.Split(':');
                if (parts.Length < 4) return "Lỗi đọc lockfile";

                using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = delegate { return true; } });
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{parts[3]}")));

                // Gọi API
                var res = await client.GetAsync($"https://127.0.0.1:{parts[2]}/chat/v1/session");

                if (res.IsSuccessStatusCode)
                {
                    string json = await res.Content.ReadAsStringAsync();

                    // Bóc tách JSON để lấy name và tag
                    using JsonDocument doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("game_name", out JsonElement nameEl) &&
                        root.TryGetProperty("game_tag", out JsonElement tagEl))
                    {
                        string gameName = nameEl.GetString();
                        string gameTag = tagEl.GetString();

                        // Lúc Riot đang quay mòng mòng đăng nhập, API vẫn trả 200 OK nhưng name sẽ rỗng
                        if (!string.IsNullOrEmpty(gameName))
                        {
                            debugLog($"Current Riot ID: {gameName}#{gameTag}");
                            return $"{gameName}#{gameTag}";
                        }
                    }
                    return "Đang tải dữ liệu...";
                }
                return $"Chưa đăng nhập";
            }
            catch (Exception ex)
            {
                debugLog($"Lỗi GetCurrentRiotId: {ex.Message}");
                return "Lỗi kết nối API";
            }
        }

        private async void BtnSelect_Click(object sender, RoutedEventArgs e)
        {
            if (LstAccounts.SelectedItem is not AccountModel acc) return;
            BtnSelect.IsEnabled = false;
            _currentState = AppState.LoggingIn;
            TxtStatus.Text = "Đang xử lý đăng nhập...";

            await Task.Run(async () =>
            {
                // Đóng Valorant
                foreach (var p in Process.GetProcessesByName("VALORANT").Concat(Process.GetProcessesByName("VALORANT-Win64-Shipping"))) p.Kill();
                await Task.Delay(1000);

                // Logout Riot Client ngầm
                await RiotApiLogoutAsync();
                await Task.Delay(1000);

                // Mở Riot Client nếu chưa chạy
                if (Process.GetProcessesByName("RiotClientServices").Length == 0)
                {
                    Process.Start(new ProcessStartInfo { FileName = _riotClientPath, UseShellExecute = true });
                }
                
                // FLAUI: Inject Account
                using var automation = new UIA3Automation();
                FlaUI.Core.AutomationElements.Window? riotWindow = null;
                for (int i = 0; i < 20; i++) // Timeout 20s
                {
                    try
                    {
                        // CÁCH MỚI: Tìm thẳng trên Desktop cửa sổ có tên "Riot Client"
                        // Không phụ thuộc vào PID hay tên file .exe nữa
                        var desktop = automation.GetDesktop();
                        riotWindow = desktop.FindFirstChild(cf => cf.ByName("Riot Client"))?.AsWindow();

                        if (riotWindow != null) break;
                    }
                    catch { }
                    await Task.Delay(500);
                }

                if (riotWindow != null)
                {
                    bool isFocused = false;
                    IntPtr riotHandle = riotWindow.Properties.NativeWindowHandle.ValueOrDefault;

                    for (int j = 0; j < 10; j++)
                    {
                        // 1. Nếu cửa sổ đang bị thu nhỏ (Minimize), ép bung nó lên (Restore)
                        if (IsIconic(riotHandle))
                        {
                            debugLog("Cửa sổ đang bị ẩn dưới Taskbar, tiến hành Restore...");
                            ShowWindow(riotHandle, SW_RESTORE);
                            await Task.Delay(500); // Chờ 1 chút cho animation của Windows chạy xong
                        }

                        // 2. Ép nó lên trên cùng
                        riotWindow.SetForeground();
                        await Task.Delay(500);

                        // 3. Kiểm tra xem đã thực sự nổi lên và KHÔNG BỊ MINIMIZE chưa
                        if (GetForegroundWindow() == riotHandle && !IsIconic(riotHandle))
                        {
                            isFocused = true;
                            break;
                        }

                        debugLog($"-> Chưa focus được (thử lại lần {j + 1}/10)...");
                    }

                    if (isFocused)
                    {
                        // Khi chắc kèo 100% cửa sổ đang nằm chình ình trên màn hình mới bắt đầu gõ
                        var userBox = riotWindow.FindFirstDescendant(cf => cf.ByAutomationId("username"))?.AsTextBox();
                        for (int i = 0; i < 20; i++)
                        {
                            if (userBox != null)
                                break;
                            else
                            {
                                debugLog($"-> Chưa tìm thấy TextBox Username (thử lại lần {i + 1}/10)...");
                                await Task.Delay(1000);
                                riotWindow = automation.GetDesktop().FindFirstChild(cf => cf.ByName("Riot Client"))?.AsWindow();
                                userBox = riotWindow?.FindFirstDescendant(cf => cf.ByAutomationId("username"))?.AsTextBox();
                            }
                        }
                        var passBox = riotWindow.FindFirstDescendant(cf => cf.ByAutomationId("password"))?.AsTextBox();
                        string realPassword = CryptoHelper.Decrypt(acc.Password);
                        var loginBtn = riotWindow.FindFirstDescendant(cf => cf.ByClassName("button icon-button disable-shadow"))?.AsButton();
                        if (userBox != null && passBox != null && loginBtn != null)
                        {
                            await Task.Delay(800);
                            userBox.Focus();
                            await Task.Delay(500);
                            userBox.Text = acc.Username;
                            //FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
                            await Task.Delay(200);
                            passBox.Focus();
                            passBox.Text = realPassword;
                            await Task.Delay(150);
                            var originalCursorPos = FlaUI.Core.Input.Mouse.Position;
                            loginBtn.Click();
                            FlaUI.Core.Input.Mouse.Position = originalCursorPos;
                            await Task.Delay(150);

                        }
                    }
                    else
                    {
                        debugLog("LỖI: Không thể mở và ép focus cửa sổ Riot Client!");
                    }
                }
                else
                {
                    debugLog("Không tìm thấy cửa sổ Riot Client!");
                }
                Dispatcher.Invoke(() =>
                {
                    if (ChkTracker.IsChecked == true && Process.GetProcessesByName("Overwolf").Length == 0)
                        Process.Start(@"C:\Program Files (x86)\Overwolf\OverwolfLauncher.exe", "-launchapp ipmlnnogholfmdmenfijjifldcpjoecappfccceh");
                });
            });
            TxtStatus.Text = "Đã đăng nhập ! Đang chờ lấy thông tin ...";
            await Task.Delay(400);
            BtnSelect.IsEnabled = true;
            // Thêm delay ban đầu 1 giây như ý bạn
            await Task.Delay(1000);

            // Vòng lặp chờ API trả về Tên (vì mạng lag có thể mất 2-5 giây)
            string riotId = "Đang tải dữ liệu...";
            for (int i = 0; i < 10; i++) // Thử tối đa 10 lần (10 giây)
            {
                riotId = await GetCurrentRiotIdAsync();

                // Nếu lấy được tên (không phải báo lỗi hay đang tải) thì thoát vòng lặp
                if (riotId.Contains("#"))
                {
                    // --- THÊM CHÍNH XÁC VÀO ĐÂY ---
                    acc.DisplayName = riotId;     // Đổi tên biến acc đang chọn
                    SaveAccountsToFile();               // Lưu ngay xuống file JSON
                    RefreshAccountList();                 // Refresh ListView để hiển thị tên mới
                    await Task.Delay(500);
                    TxtStatus.Text = $"Hoàn tất: {riotId}";
                    break;
                }
                TxtStatus.Text = riotId;

                debugLog($"Chưa lấy được tên (thử lại lần {i + 1}/10)...");
                await Task.Delay(1000); // Mỗi lần check cách nhau 1 giây
            }            

            // Chờ 3 giây để người dùng ngắm cái tên rồi mới mở lại Timer quét tiến trình
            await Task.Delay(3000);
            _currentState = AppState.Idle;
        }
    }
}