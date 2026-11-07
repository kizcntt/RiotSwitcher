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
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Security.Principal;

namespace RiotSwitcher
{
    public enum AppState
    {
        Idle,
        LoggingIn
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
        private string _currentLanguage = "vi"; // Mặc định nếu chưa có cấu hình
        private const string SettingsFile = "setting.ini";
        private readonly string _lockfilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Riot Games\Riot Client\Config\lockfile");
        private readonly string _dbFilePath = "accounts.json";
        private const string RankCacheFile = "rank_cache.json";
        private Dictionary<string, RankCacheItem> _rankCache = new Dictionary<string, RankCacheItem>();
        private AppState _currentState = AppState.Idle;
        private static readonly HttpClient _httpClient = new HttpClient();

        private List<AccountModel> _accounts = new();
        private bool _isEditing = false;
        private DispatcherTimer _processTimer;

        public MainWindow()
        {
            if (!IsAdministrator())
            {
                MessageBox.Show(Localization.Get("InsufficientPermissions"),
                                Localization.Get("InsufficientPermissionsTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown();
                return;
            }

            InitializeComponent();

            // Đảm bảo UI đã dựng xong hoàn toàn mới tick chọn nút theo ngôn ngữ đã lưu
            this.Loaded += (s, e) =>
            {
                if (_currentLanguage.Equals("en", StringComparison.OrdinalIgnoreCase))
                {
                    RadEnglish.IsChecked = true;
                }
                else
                {
                    RadVietnamese.IsChecked = true;
                }
            };

            Thread.Sleep(500);
            InitializeSettings();
            LoadAccounts();
            _ = LoadAllRanksAsync();
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
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveSettingsToFile();
            base.OnClosing(e);
        }

        private void LanguageRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.RadioButton radioButton && radioButton.Tag is string language)
            {
                _currentLanguage = language;
                Localization.SetLanguage(language);
                if (LblFormTitle != null)
                {
                    LblFormTitle.Text = Localization.Get(_isEditing ? "UpdateAccount" : "AddAccount");
                }
            }
        }

        private void InitProcessCheckTimer()
        {
            _processTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _processTimer.Tick += (s, e) =>
            {
                if (_currentState != AppState.Idle) return;
                if (Process.GetProcessesByName("VALORANT").Length > 0) TxtStatus.Text = Localization.Get("ValorantRunning");
                else if (Process.GetProcessesByName("RiotClientServices").Length > 0) TxtStatus.Text = Localization.Get("RiotClientRunning");
                else TxtStatus.Text = Localization.Get("WaitingRiotClient");
            };
            _processTimer.Start();
        }

        private void debugLog(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[DEBUG] {message}");
        }

        private void InitializeSettings()
        {
            // 1. KIỂM TRA VÀ ĐỌC FILE SETTING.INI NẾU ĐÃ CÓ
            if (File.Exists(SettingsFile))
            {
                var lines = File.ReadAllLines(SettingsFile);
                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("RiotClientPath=", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = line.Substring("RiotClientPath=".Length).Trim();
                        if (File.Exists(path))
                        {
                            _riotClientPath = path;
                        }
                    }
                    else if (line.StartsWith("Language=", StringComparison.OrdinalIgnoreCase))
                    {
                        string lang = line.Substring("Language=".Length).Trim();
                        if (!string.IsNullOrEmpty(lang))
                        {
                            _currentLanguage = lang;
                        }
                    }
                }

                Localization.SetLanguage(_currentLanguage);

                if (!string.IsNullOrEmpty(_riotClientPath) && File.Exists(_riotClientPath))
                {
                    return;
                }
            }
            else
            {
                // Lần đầu mở app: Mặc định là tiếng Việt
                _currentLanguage = "vi";
                Localization.SetLanguage(_currentLanguage);
            }

            // 2. NẾU CHƯA CÓ PATH -> QUÉT PROCESS
            _riotClientPath = GetPathFromRunningProcess();

            // 3. NẾU PROCESS KHÔNG CHẠY -> MỞ DIALOG BẮT CHỌN
            if (string.IsNullOrEmpty(_riotClientPath) || !File.Exists(_riotClientPath))
            {
                MessageBox.Show(Localization.Get("FirstRun"),
                                Localization.Get("InitialSetup"), MessageBoxButton.OK, MessageBoxImage.Information);

                OpenFileDialog openFileDialog = new OpenFileDialog();
                openFileDialog.Filter = "Riot Client (RiotClientServices.exe)|RiotClientServices.exe";
                openFileDialog.Title = Localization.Get("SelectRiotClient");

                if (openFileDialog.ShowDialog() == true)
                {
                    _riotClientPath = openFileDialog.FileName;
                }
                else
                {
                    MessageBox.Show(Localization.Get("RequiredPath"), Localization.Get("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                    System.Windows.Application.Current.Shutdown();
                    return;
                }
            }

            // 4. LƯU LẠI VÀO SETTING.INI CHO LẦN SAU
            SaveSettingsToFile();
        }

        private void SaveSettingsToFile()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"RiotClientPath={_riotClientPath}");
                sb.AppendLine($"Language={_currentLanguage}");
                File.WriteAllText(SettingsFile, sb.ToString());
            }
            catch (Exception ex)
            {
                debugLog($"Không thể lưu setting.ini: {ex.Message}");
            }
        }


        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj == null) yield break;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(depObj, i);
                if (child is T match)
                {
                    yield return match;
                }
                foreach (T childOfChild in FindVisualChildren<T>(child))
                {
                    yield return childOfChild;
                }
            }
        }

        // Hàm hỗ trợ: Trích xuất đường dẫn từ Process đang chạy
        private string GetPathFromRunningProcess()
        {
            try
            {
                var processes = Process.GetProcessesByName("RiotClientServices");
                if (processes.Length > 0)
                {
                    return processes[0].MainModule.FileName;
                }
            }
            catch (Exception ex)
            {
                debugLog($"Không thể lấy đường dẫn từ process: {ex.Message}");
            }
            return null;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

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

            foreach (var acc in _accounts)
            {
                if (!acc.DisplayName.Contains("#"))
                {
                    acc.Rank = Localization.Get("Unranked");
                    acc.RankColor = "#585B70";
                    continue;
                }

                if (_rankCache.TryGetValue(acc.DisplayName, out var cachedData))
                {
                    acc.Rank = cachedData.Rank;
                    acc.RankColor = GetRankColor(cachedData.Rank);
                }
                else
                {
                    acc.Rank = Localization.Get("Loading");
                    acc.RankColor = "#A6ADC8";
                }
            }

            LstAccounts.Items.Refresh();

            bool hasUpdate = false;
            foreach (var acc in _accounts)
            {
                if (!acc.DisplayName.Contains("#")) continue;

                if (_rankCache.TryGetValue(acc.DisplayName, out var cachedData) && DateTime.Now < cachedData.ExpireTime)
                {
                    continue;
                }

                string newRank = await GetRankFromTrackerAsync(acc.DisplayName);

                if (acc.Rank != newRank)
                {
                    acc.Rank = newRank;
                    acc.RankColor = GetRankColor(newRank);
                    hasUpdate = true;
                    LstAccounts.Items.Refresh();
                }
            }

            if (hasUpdate)
            {
                LstAccounts.Items.Refresh();
            }
        }

        private string GetRankColor(string currentRank)
        {
            if (currentRank.Contains("Iron") || currentRank.Contains("Bronze") || currentRank.Contains("Silver"))
                return "#A6ADC8";
            if (currentRank.Contains("Gold"))
                return "#F9E2AF";
            if (currentRank.Contains("Platinum"))
                return "#89B4FA";
            if (currentRank.Contains("Diamond"))
                return "#CBA6F7";
            if (currentRank.Contains("Ascendant"))
                return "#A6E3A1";
            if (currentRank.Contains("Immortal"))
                return "#F38BA8";
            if (currentRank.Contains("Radiant"))
                return "#FAB387";
            return "#585B70";
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
            await Task.Delay(1000);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "RiotAccountSwitcher_Project");

            try
            {
                string urlKyros = $"https://api.kyroskoh.xyz/valorant/v1/mmr/ap/{name}/{tag}?show=rankonly&display=0";
                var resKyros = await _httpClient.GetAsync(urlKyros);

                if (resKyros.IsSuccessStatusCode)
                {
                    string resultText = await resKyros.Content.ReadAsStringAsync();
                    string cleanRank = resultText.Replace(".", "").Trim();

                    if (!cleanRank.Contains("API Error") && !cleanRank.ToLower().Contains("error") && !string.IsNullOrEmpty(cleanRank))
                    {
                        finalRank = cleanRank;
                        goto SaveAndReturn;
                    }
                }
            }
            catch { finalRank = "Busy"; }

        SaveAndReturn:
            if (finalRank == "Unranked" || finalRank == "Busy" || finalRank.StartsWith("Lỗi") || finalRank.Contains("API Error"))
            {
                // Bỏ qua không lưu cache
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
            private const string SecretKey = "riotShuAkashi";

            public static string Encrypt(string plainText)
            {
                if (string.IsNullOrEmpty(plainText)) return string.Empty;

                char[] xorResult = new char[plainText.Length];
                for (int i = 0; i < plainText.Length; i++)
                {
                    xorResult[i] = (char)(plainText[i] ^ SecretKey[i % SecretKey.Length]);
                }

                Array.Reverse(xorResult);

                byte[] bytes = Encoding.UTF8.GetBytes(new string(xorResult));
                return Convert.ToBase64String(bytes);
            }

            public static string Decrypt(string encodedText)
            {
                if (string.IsNullOrEmpty(encodedText)) return string.Empty;
                try
                {
                    byte[] bytes = Convert.FromBase64String(encodedText);
                    char[] chars = Encoding.UTF8.GetString(bytes).ToCharArray();

                    Array.Reverse(chars);

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
            LblFormTitle.Text = Localization.Get("AddAccount");
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
                if (LstAccounts.SelectedItem is AccountModel acc)
                {
                    acc.Username = TxtUsername.Text;
                    acc.Password = CryptoHelper.Encrypt(TxtPassword.Password);
                }
            }
            else
            {
                string tempDisplayName = TxtUsername.Text;

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
                LblFormTitle.Text = Localization.Get("UpdateAccount");
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (LstAccounts.SelectedItem is AccountModel acc && MessageBox.Show(Localization.Get("ConfirmDelete", acc.DisplayName), Localization.Get("Confirm"), MessageBoxButton.YesNo) == MessageBoxResult.Yes)
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
            if (!File.Exists(_lockfilePath)) return Localization.Get("WaitingRiotClient");
            try
            {
                using var fs = new FileStream(_lockfilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string lockContent = await sr.ReadToEndAsync();

                string[] parts = lockContent.Split(':');
                if (parts.Length < 4) return Localization.Get("Error");

                using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = delegate { return true; } });
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{parts[3]}")));

                var res = await client.GetAsync($"https://127.0.0.1:{parts[2]}/chat/v1/session");

                if (res.IsSuccessStatusCode)
                {
                    string json = await res.Content.ReadAsStringAsync();

                    using JsonDocument doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("game_name", out JsonElement nameEl) &&
                        root.TryGetProperty("game_tag", out JsonElement tagEl))
                    {
                        string gameName = nameEl.GetString();
                        string gameTag = tagEl.GetString();

                        if (!string.IsNullOrEmpty(gameName))
                        {
                            debugLog($"Current Riot ID: {gameName}#{gameTag}");
                            return $"{gameName}#{gameTag}";
                        }
                    }
                    return Localization.Get("Loading");
                }
                return Localization.Get("Error");
            }
            catch (Exception ex)
            {
                debugLog($"Lỗi GetCurrentRiotId: {ex.Message}");
                return Localization.Get("Error");
            }
        }

        private async void BtnSelect_Click(object sender, RoutedEventArgs e)
        {
            if (LstAccounts.SelectedItem is not AccountModel acc) return;
            BtnSelect.IsEnabled = false;
            _currentState = AppState.LoggingIn;
            TxtStatus.Text = Localization.Get("ProcessingLogin");

            await Task.Run(async () =>
            {
                foreach (var p in Process.GetProcessesByName("VALORANT").Concat(Process.GetProcessesByName("VALORANT-Win64-Shipping"))) p.Kill();
                await Task.Delay(1000);

                await RiotApiLogoutAsync();
                await Task.Delay(1000);

                if (Process.GetProcessesByName("RiotClientServices").Length == 0)
                {
                    Process.Start(new ProcessStartInfo { FileName = _riotClientPath, UseShellExecute = true });
                }

                using var automation = new UIA3Automation();
                FlaUI.Core.AutomationElements.Window? riotWindow = null;
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
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
                        if (IsIconic(riotHandle))
                        {
                            debugLog("Cửa sổ đang bị ẩn dưới Taskbar, tiến hành Restore...");
                            ShowWindow(riotHandle, SW_RESTORE);
                            await Task.Delay(500);
                        }

                        riotWindow.SetForeground();
                        await Task.Delay(500);

                        if (GetForegroundWindow() == riotHandle && !IsIconic(riotHandle))
                        {
                            isFocused = true;
                            break;
                        }

                        debugLog($"-> Chưa focus được (thử lại lần {j + 1}/10)...");
                    }

                    if (isFocused)
                    {
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
            });

            TxtStatus.Text = Localization.Get("LoggedInWaiting");
            await Task.Delay(400);
            BtnSelect.IsEnabled = true;
            await Task.Delay(1000);

            string riotId = Localization.Get("Loading");
            for (int i = 0; i < 10; i++)
            {
                riotId = await GetCurrentRiotIdAsync();

                if (riotId.Contains("#"))
                {
                    acc.DisplayName = riotId;
                    SaveAccountsToFile();
                    RefreshAccountList();
                    await Task.Delay(500);
                    TxtStatus.Text = Localization.Get("Completed", riotId);
                    break;
                }
                TxtStatus.Text = riotId;

                debugLog($"Chưa lấy được tên (thử lại lần {i + 1}/10)...");
                await Task.Delay(1000);
            }

            await Task.Delay(3000);
            _currentState = AppState.Idle;
        }
    }
}