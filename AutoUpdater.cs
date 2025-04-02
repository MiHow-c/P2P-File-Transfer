using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
// Upewnij się, że masz dostęp do System.Linq dla FirstOrDefault
using System.Linq;

public static class AutoUpdater
{
    // --- Konfiguracja ---
    private const string GitHubOwner = "MiHow-c"; // <-- ZMIEŃ
    private const string GitHubRepo = "P2P-File-Transfer"; // <-- ZMIEŃ
    private const string ReleaseAssetNamePattern = ".zip"; // Lub ".zip"
    private const string UpdaterExecutableName = "Updater.exe"; // Nazwa pliku wykonywalnego aktualizatora

    // --- Prywatne ---
    private static readonly HttpClient httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("P2PFileTransferUpdater", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0"));
        return client;
    }

    // Metoda CheckForUpdatesAsync pozostaje bez zmian (z poprzedniej odpowiedzi)
    public static async Task CheckForUpdatesAsync()
    {
        Console.WriteLine("[Updater] Sprawdzanie dostępności aktualizacji...");

        try
        {
            string apiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            HttpResponseMessage response = await httpClient.GetAsync(apiUrl);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Updater BŁĄD] Nie można pobrać informacji o wydaniu: {response.StatusCode}");
                return;
            }

            string jsonContent = await response.Content.ReadAsStringAsync();
            using JsonDocument document = JsonDocument.Parse(jsonContent);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("tag_name", out JsonElement tagNameElement) || tagNameElement.ValueKind != JsonValueKind.String)
            {
                Console.WriteLine("[Updater BŁĄD] Nie znaleziono 'tag_name' w odpowiedzi API GitHub.");
                return;
            }
            string latestVersionStr = tagNameElement.GetString() ?? "";
            if (latestVersionStr.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                latestVersionStr = latestVersionStr.Substring(1);
            }

            if (!Version.TryParse(latestVersionStr, out Version? latestVersion) || latestVersion == null)
            {
                Console.WriteLine($"[Updater BŁĄD] Nieprawidłowy format wersji w tagu: {tagNameElement.GetString()}");
                return;
            }

            Version? currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (currentVersion == null)
            {
                Console.WriteLine("[Updater BŁĄD] Nie można odczytać bieżącej wersji aplikacji.");
                return;
            }

            Console.WriteLine($"[Updater] Aktualna wersja: {currentVersion}, Najnowsza wersja: {latestVersion}");

            if (latestVersion > currentVersion)
            {
                Console.WriteLine("[Updater] Dostępna nowa wersja!");

                if (!root.TryGetProperty("assets", out JsonElement assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
                {
                    Console.WriteLine("[Updater BŁĄD] Nie znaleziono sekcji 'assets' w odpowiedzi API.");
                    return;
                }

                string? downloadUrl = null;
                string? assetFileName = null;

                foreach (JsonElement asset in assetsElement.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String &&
                        asset.TryGetProperty("browser_download_url", out JsonElement urlElement) && urlElement.ValueKind == JsonValueKind.String)
                    {
                        string currentAssetName = nameElement.GetString() ?? "";
                        if (currentAssetName.EndsWith(ReleaseAssetNamePattern, StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = urlElement.GetString();
                            assetFileName = currentAssetName;
                            Console.WriteLine($"[Updater] Znaleziono asset do pobrania: {assetFileName}");
                            break;
                        }
                    }
                }

                if (downloadUrl == null || assetFileName == null)
                {
                    Console.WriteLine($"[Updater BŁĄD] Nie znaleziono pasującego pliku ('*{ReleaseAssetNamePattern}') w wydaniu {latestVersion}.");
                    return;
                }

                Console.Write($"[Updater] Czy chcesz pobrać i zainstalować wersję {latestVersion}? [T/N]: ");
                string? input = Console.ReadLine();
                if (!string.Equals(input?.Trim(), "T", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[Updater] Anulowano aktualizację.");
                    return;
                }

                // Zmienione: Wywołaj pobieranie i uruchomienie zewnętrznego Updatera
                await DownloadAndUpdateUsingExternalUpdater(downloadUrl, assetFileName);
            }
            else
            {
                Console.WriteLine("[Updater] Używasz najnowszej wersji.");
            }
        }
        catch (HttpRequestException ex) { Console.WriteLine($"[Updater BŁĄD Sieci] {ex.Message}"); }
        catch (JsonException ex) { Console.WriteLine($"[Updater BŁĄD JSON] {ex.Message}"); }
        catch (Exception ex) { Console.WriteLine($"[Updater BŁĄD Ogólny] {ex.ToString()}"); }
    }


    // --- ZMODYFIKOWANA METODA ---
    private static async Task DownloadAndUpdateUsingExternalUpdater(string downloadUrl, string assetFileName)
    {
        string? currentExePath = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrEmpty(currentExePath))
        {
            Console.WriteLine("[Updater BŁĄD] Nie można uzyskać ścieżki do bieżącego pliku wykonywalnego.");
            return;
        }
        string currentExeName = Path.GetFileName(currentExePath); // np. "Server.exe"
        string? directory = Path.GetDirectoryName(currentExePath);
        if (string.IsNullOrEmpty(directory))
        {
            Console.WriteLine("[Updater BŁĄD] Nie można uzyskać katalogu aplikacji.");
            return;
        }

        // Ścieżka do zewnętrznego Updatera (zakładamy, że jest w tym samym katalogu)
        string updaterPath = Path.Combine(directory, UpdaterExecutableName);
        if (!File.Exists(updaterPath))
        {
            Console.WriteLine($"[Updater BŁĄD] Nie znaleziono pliku aktualizatora: {updaterPath}");
            Console.WriteLine($"[Updater BŁĄD] Upewnij się, że {UpdaterExecutableName} znajduje się w tym samym katalogu co aplikacja.");
            return;
        }


        // Ścieżka do tymczasowego pliku dla pobranej aktualizacji
        // Użyj unikalnej nazwy, aby uniknąć konfliktów
        string tempDownloadedFilePath = Path.Combine(directory, $"_update_temp_{Guid.NewGuid()}{Path.GetExtension(assetFileName)}");

        Console.WriteLine($"[Updater] Pobieranie {assetFileName} z {downloadUrl}...");
        try
        {
            // 1. Pobierz aktualizację
            using (HttpResponseMessage downloadResponse = await httpClient.GetAsync(downloadUrl))
            {
                downloadResponse.EnsureSuccessStatusCode();
                using (Stream contentStream = await downloadResponse.Content.ReadAsStreamAsync())
                using (FileStream fileStream = new FileStream(tempDownloadedFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await contentStream.CopyToAsync(fileStream);
                    Console.WriteLine($"[Updater] Plik pobrany do: {tempDownloadedFilePath}");
                }
            }

            // 2. Rozpakuj, jeśli to ZIP
            string finalNewExePath = tempDownloadedFilePath; // Domyślnie ścieżka do pobranego pliku
            string? tempExtractDir = null; // Do przechowywania ścieżki do katalogu tymczasowego

            if (assetFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                tempExtractDir = Path.Combine(directory, $"_update_extract_{Guid.NewGuid()}");
                if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, true);
                Directory.CreateDirectory(tempExtractDir);

                Console.WriteLine($"[Updater] Rozpakowywanie {tempDownloadedFilePath} do {tempExtractDir}...");
                ZipFile.ExtractToDirectory(tempDownloadedFilePath, tempExtractDir);

                // Znajdź plik .exe w rozpakowanym katalogu
                // Preferuj plik o takiej samej nazwie jak bieżący
                string? extractedExe = Directory.GetFiles(tempExtractDir, currentExeName, SearchOption.AllDirectories).FirstOrDefault();
                if (extractedExe == null)
                {
                    // Jeśli nie ma, weź pierwszy napotkany plik .exe
                    extractedExe = Directory.GetFiles(tempExtractDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (extractedExe == null)
                    {
                        Console.WriteLine($"[Updater BŁĄD] Nie znaleziono pliku wykonywalnego w pobranym archiwum ZIP.");
                        Directory.Delete(tempExtractDir, true); // Posprzątaj
                        File.Delete(tempDownloadedFilePath); // Usuń zip
                        return;
                    }
                    Console.WriteLine($"[Updater UWAGA] W ZIP nie znaleziono '{currentExeName}', używam znalezionego '{Path.GetFileName(extractedExe)}'.");
                }
                finalNewExePath = extractedExe; // To jest nasz nowy plik exe do podmiany
                Console.WriteLine($"[Updater] Rozpakowano, nowy plik: {finalNewExePath}");
            }

            // 3. Przygotuj argumenty i uruchom Updater.exe
            int currentPid = Process.GetCurrentProcess().Id;
            // Argumenty muszą być często w cudzysłowach, zwłaszcza ścieżki ze spacjami
            string args = $"\"{currentPid}\" \"{currentExePath}\" \"{finalNewExePath}\"";

            Console.WriteLine($"[Updater] Uruchamianie zewnętrznego aktualizatora: {updaterPath}");
            Log($"Uruchamianie: {UpdaterExecutableName} {args}"); // Dodatkowe logowanie

            ProcessStartInfo startInfo = new ProcessStartInfo(updaterPath)
            {
                Arguments = args,
                UseShellExecute = true, // Może pomóc w kwestii uprawnień
                // Opcjonalnie: Uruchom jako administrator, jeśli to konieczne
                // Verb = "runas",
                WorkingDirectory = directory // Ustaw katalog roboczy dla Updatera
            };

            try
            {
                Process.Start(startInfo);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (UAC)
            {
                Console.WriteLine("[Updater BŁĄD] Użytkownik anulował żądanie podniesienia uprawnień (UAC).");
                Log("Uruchomienie Updatera anulowane przez UAC.");
                // Posprzątaj: usuń pobrany plik i ewentualnie rozpakowany katalog
                if (File.Exists(tempDownloadedFilePath)) File.Delete(tempDownloadedFilePath);
                if (tempExtractDir != null && Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, true);
                return; // Nie zamykaj głównej aplikacji
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Updater BŁĄD] Nie można uruchomić {UpdaterExecutableName}: {ex.Message}");
                Log($"Błąd uruchamiania Updatera: {ex.ToString()}");
                // Posprzątaj
                if (File.Exists(tempDownloadedFilePath)) File.Delete(tempDownloadedFilePath);
                if (tempExtractDir != null && Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, true);
                return; // Nie zamykaj głównej aplikacji
            }


            // 4. Zamknij bieżącą aplikację (Server.exe), aby Updater mógł działać
            Console.WriteLine("[Updater] Zamykanie bieżącej aplikacji w celu dokończenia aktualizacji...");
            Log("Główna aplikacja (Server.exe) zostanie teraz zamknięta.");
            Environment.Exit(0); // Zakończ działanie Server.exe
        }
        catch (HttpRequestException ex) { Console.WriteLine($"[Updater BŁĄD Sieci] Błąd podczas pobierania: {ex.Message}"); HandleDownloadError(tempDownloadedFilePath, null); }
        catch (UnauthorizedAccessException ex) { Console.WriteLine($"[Updater BŁĄD Uprawnień] Brak uprawnień do zapisu plików tymczasowych: {ex.Message}"); HandleDownloadError(tempDownloadedFilePath, null); }
        catch (Exception ex) { Console.WriteLine($"[Updater BŁĄD] Nieoczekiwany błąd: {ex.ToString()}"); HandleDownloadError(tempDownloadedFilePath, null); }
        // Jeśli wystąpi błąd PRZED uruchomieniem Updatera, usuń pobrane pliki
    }

    // Metoda pomocnicza do sprzątania po błędzie pobierania/rozpakowania
    private static void HandleDownloadError(string tempFilePath, string? extractDir)
    {
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
                Console.WriteLine($"[Updater] Usunięto tymczasowy plik: {tempFilePath}");
                Log($"Usunięto plik tymczasowy {tempFilePath} po błędzie.");
            }
            if (!string.IsNullOrEmpty(extractDir) && Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, true);
                Console.WriteLine($"[Updater] Usunięto tymczasowy katalog rozpakowania: {extractDir}");
                Log($"Usunięto katalog tymczasowy {extractDir} po błędzie.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Updater OSTRZEŻENIE] Błąd podczas sprzątania plików tymczasowych: {ex.Message}");
            Log($"Błąd podczas sprzątania po błędzie: {ex.Message}");
        }
    }

    // Metoda CleanupOldFiles (z poprzedniej odpowiedzi) - może pozostać, aby sprzątać stare pliki .old przy starcie
    public static void CleanupOldFiles()
    {
        string? currentExePath = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrEmpty(currentExePath)) return;
        string? directory = Path.GetDirectoryName(currentExePath);
        if (string.IsNullOrEmpty(directory)) return;

        // Usuwanie starych plików .old głównej aplikacji
        string oldExePath = currentExePath + ".old";
        if (File.Exists(oldExePath))
        {
            try
            {
                Console.WriteLine($"[Startup Cleanup] Usuwanie starego pliku aplikacji: {oldExePath}");
                File.Delete(oldExePath);
                Log($"Usunięto stary plik aplikacji: {oldExePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup Cleanup OSTRZEŻENIE] Nie można usunąć {oldExePath}: {ex.Message}.");
                Log($"Nie udało się usunąć {oldExePath}: {ex.Message}");
            }
        }

        // Opcjonalnie: Usuwanie starych logów Updatera (np. starszych niż X dni)
        try
        {
            string logPattern = "Updater*.log"; // Wzorzec dla plików logów
            var logFiles = Directory.GetFiles(directory, logPattern);
            foreach (var logFile in logFiles)
            {
                FileInfo fi = new FileInfo(logFile);
                // Usuń logi starsze niż 7 dni (można dostosować)
                if (fi.LastWriteTime < DateTime.Now.AddDays(-7))
                {
                    Log($"Usuwanie starego pliku logu: {logFile}");
                    fi.Delete();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup Cleanup OSTRZEŻENIE] Błąd podczas czyszczenia starych logów: {ex.Message}");
            Log($"Błąd podczas czyszczenia starych logów: {ex.Message}");
        }


    }

    // Proste logowanie również dla głównej aplikacji (jeśli potrzebne)
    private static void Log(string message)
    {
        try
        {
            string logFilePath = Path.Combine(AppContext.BaseDirectory, "Server_AutoUpdate.log");
            using (StreamWriter sw = new StreamWriter(logFilePath, true, System.Text.Encoding.UTF8))
            {
                sw.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
            }
        }
        catch { /* Ignoruj błędy logowania */ }
    }
}

// W pliku Program.cs projektu Server, metoda Main pozostaje taka sama jak w poprzedniej odpowiedzi:
// public static async Task Main(string[] args)
// {
//     Console.WriteLine("--- Aplikacja P2P do Transferu Plikow ---");
//
//     if (!args.Contains("--no-update", StringComparer.OrdinalIgnoreCase))
//     {
//          AutoUpdater.CleanupOldFiles(); // Czyści stare pliki .old
//          await AutoUpdater.CheckForUpdatesAsync(); // Sprawdza i ewentualnie uruchamia Updater.exe
//     }
//     else { Console.WriteLine("[INFO] Pominięto sprawdzanie aktualizacji (--no-update)."); }
//
//     Directory.CreateDirectory(DOWNLOAD_DIR);
//     Console.Write("Uruchomic jako (S)erwer (nasluchuje) czy (K)lient (laczy sie)? [S/K]: ");
//     // ... reszta kodu ...
// }