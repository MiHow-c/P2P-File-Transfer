using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class P2PFileTransfer
{
    // --- Konfiguracja Domyślna ---
    private const string DEFAULT_SERVER_IP = "146.70.161.190"; // IP z Proton VPN (dla klienta)
    private const string DEFAULT_LISTEN_IP = "0.0.0.0";       // Domyślny IP nasłuchu (wszystkie interfejsy)
    private const int DEFAULT_PORT = 32897;         // Port z Proton VPN
    private const int BUFFER_SIZE = 8192;          // Bufor dla plikow
    private const string DOWNLOAD_DIR = "P2P_Downloads"; // Katalog na pobrane pliki

    // --- Stale protokolu ---
    private const char SEPARATOR = '|';
    private const string CMD_SENDFILE = "SENDFILE";
    private const string CMD_FILEACCEPT = "FILEACCEPT";
    private const string CMD_FILEREJECT = "FILEREJECT";
    private const string CMD_TRANSFERCOMPLETE = "TRANSFERCOMPLETE";
    private const string CMD_TRANSFERERROR = "TRANSFERERROR";
    private const string CMD_DISCONNECT = "!disconnect";

    // --- Stan transferu (wspoldzielony) ---
    private static volatile bool isTransferInProgress = false;
    // Uzywamy ? dla typu nullowalnego
    private static TaskCompletionSource<bool>? fileAcceptanceTcs;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("--- Aplikacja P2P do Transferu Plikow ---");
        Directory.CreateDirectory(DOWNLOAD_DIR); // Utworz katalog na pobrane pliki

        if (!args.Contains("--no-update", StringComparer.OrdinalIgnoreCase))
        {
            // Najpierw wyczyść stare pliki .old
            AutoUpdater.CleanupOldFiles();
            // Potem sprawdź aktualizacje
            await AutoUpdater.CheckForUpdatesAsync();
            // CheckForUpdatesAsync zamknie aplikację, jeśli zainstaluje aktualizację (Environment.Exit)
        }
        else
        {
            Console.WriteLine("[INFO] Pominięto sprawdzanie aktualizacji (--no-update).");
        }

        Console.Write("Uruchomic jako (S)erwer (nasluchuje) czy (K)lient (laczy sie)? [S/K]: ");
        // Uzywamy string? dla wyniku ReadLine()
        string? mode = Console.ReadLine()?.ToUpperInvariant();

        try
        {
            if (mode == "S")
            {
                // --- Tryb Serwera ---
                string listenIp = GetIpInput($"Wpisz lokalny adres IP do nasluchiwania (domyslnie: {DEFAULT_LISTEN_IP} - wszystkie): ", DEFAULT_LISTEN_IP, true);
                int port = GetPortInput($"Wpisz port do nasluchiwania (domyslnie: {DEFAULT_PORT}): ", DEFAULT_PORT);
                await StartServerAsync(listenIp, port);
            }
            else if (mode == "K")
            {
                // --- Tryb Klienta ---
                string serverIp = GetIpInput($"Wpisz adres IP serwera (domyslnie: {DEFAULT_SERVER_IP}): ", DEFAULT_SERVER_IP, false);
                int serverPort = GetPortInput($"Wpisz port serwera (domyslnie: {DEFAULT_PORT}): ", DEFAULT_PORT);
                await StartClientAsync(serverIp, serverPort);
            }
            else
            {
                Console.WriteLine("Nieprawidlowy wybor trybu.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLAD KRYTYCZNY] Wystapil nieoczekiwany blad: {ex.ToString()}");
        }
        finally
        {
            Console.WriteLine("\nAplikacja zakonczyla dzialanie. Nacisnij Enter, aby zamknac...");
            Console.ReadLine();
        }
    }

    // --- Logika Serwera (Nasluchiwanie) ---
    private static async Task StartServerAsync(string listenIpAddress, int port)
    {
        TcpListener? server = null; // Uzywamy TcpListener?
        TcpClient? client = null; // Uzywamy TcpClient?
        try
        {
            if (!IPAddress.TryParse(listenIpAddress, out IPAddress? parsedIp)) // Uzywamy IPAddress?
            {
                Console.WriteLine($"[BLAD] Nieprawidlowy format adresu IP: {listenIpAddress}. Uzywam domyslnego {DEFAULT_LISTEN_IP}.");
                parsedIp = IPAddress.Any;
                listenIpAddress = DEFAULT_LISTEN_IP;
            }
            // Sprawdzamy null przed uzyciem, chociaz TryParse powinien zwrocic false jesli sie nie uda
            if (parsedIp == null)
            {
                Console.WriteLine($"[BLAD] Nie udalo sie sparsowac adresu IP. Uzywam {DEFAULT_LISTEN_IP}.");
                parsedIp = IPAddress.Any;
                listenIpAddress = DEFAULT_LISTEN_IP;
            }

            server = new TcpListener(parsedIp, port);
            server.Start();
            Console.WriteLine($"[SERWER] Nasluchuje na {listenIpAddress}:{port}...");
            Console.WriteLine("Oczekiwanie na polaczenie klienta...");

            client = await server.AcceptTcpClientAsync();
            server.Stop(); // Przestan nasluchiwac po polaczeniu

            var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            // Zapewniamy, ze peerIp nie jest null
            string peerIp = remoteEndPoint != null ? $"{remoteEndPoint.Address}:{remoteEndPoint.Port}" : "nieznany adres";
            Console.WriteLine($"[SERWER] Polaczono z klientem: {peerIp}");

            await HandlePeerConnectionAsync(client, peerIp);
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[BLAD SERWERA SOCKET] {ex.Message} (Czy port {port} jest wolny?)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLAD SERWERA] {ex.ToString()}");
        }
        finally
        {
            server?.Stop(); // Uzywamy ?.
            client?.Close(); // Uzywamy ?.
        }
    }

    // --- Logika Klienta (Laczenie) ---
    private static async Task StartClientAsync(string serverIp, int serverPort)
    {
        TcpClient? client = null; // Uzywamy TcpClient?
        try
        {
            client = new TcpClient();
            Console.WriteLine($"[KLIENT] Laczenie z {serverIp}:{serverPort}...");
            var connectTask = client.ConnectAsync(serverIp, serverPort);
            if (await Task.WhenAny(connectTask, Task.Delay(15000)) != connectTask)
            {
                throw new SocketException(10060); // Timeout
            }
            await connectTask; // Rzuc ewentualny wyjatek polaczenia

            var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            // Zapewniamy, ze peerIp nie jest null
            string peerIp = remoteEndPoint != null ? $"{remoteEndPoint.Address}:{remoteEndPoint.Port}" : "nieznany adres";
            Console.WriteLine($"[KLIENT] Polaczono z serwerem: {peerIp}");

            await HandlePeerConnectionAsync(client, peerIp);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            Console.WriteLine($"[BLAD KLIENTA SOCKET] Nie mozna polaczyc sie z serwerem: Przekroczono limit czasu.");
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[BLAD KLIENTA SOCKET] {ex.Message} (Kod: {ex.SocketErrorCode})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLAD KLIENTA] {ex.ToString()}");
        }
        // HandlePeerConnectionAsync samo zarzadza zamknieciem klienta w bloku using
    }

    // --- Wspolna Logika Obslugi Polaczenia P2P ---
    private static async Task HandlePeerConnectionAsync(TcpClient client, string peerIp)
    {
        Console.WriteLine($"[INFO] Nawiazano polaczenie P2P z {peerIp}.");
        Console.WriteLine($"Pliki beda zapisywane w: {Path.GetFullPath(DOWNLOAD_DIR)}");
        Console.WriteLine("Dostepne komendy: 'send <sciezka_do_pliku>', 'exit'");

        using (client)
        using (NetworkStream stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
        using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true })
        {
            CancellationTokenSource cts = new CancellationTokenSource();

            // Uruchom nasluchiwanie na komendy od partnera w tle
            var receiveTask = Task.Run(() => ReceivePeerCommandsAsync(reader, writer, stream, peerIp, cts.Token), cts.Token);

            try
            {
                // Glowna petla komend uzytkownika
                while (client.Connected && !cts.Token.IsCancellationRequested)
                {
                    Console.Write("\n> Wpisz komende: ");
                    // Uzywamy string? dla wyniku ReadLine()
                    string? input = await Task.Run(() => Console.ReadLine());

                    if (input == null) // Jawne sprawdzenie null
                    {
                        if (!cts.IsCancellationRequested) cts.Cancel();
                        break;
                    }

                    string[] parts = input.Trim().Split(' ', 2);
                    string command = parts[0].ToLowerInvariant();

                    if (command == "exit" || command == CMD_DISCONNECT)
                    {
                        if (isTransferInProgress)
                        {
                            Console.WriteLine("[INFO] Nie mozna sie rozlaczyc, transfer pliku jest w toku.");
                            continue;
                        }
                        try { await writer.WriteLineAsync(CMD_DISCONNECT); } catch { /* Ignoruj blad przy rozlaczaniu */ }
                        if (!cts.IsCancellationRequested) cts.Cancel(); // Anuluj nasluchiwanie
                        break;
                    }
                    else if (command == "send" && parts.Length == 2)
                    {
                        if (isTransferInProgress)
                        {
                            Console.WriteLine("[INFO] Inny transfer jest juz w toku. Poczekaj.");
                            continue;
                        }
                        // Przekaz stream i writer
                        await SendFileCommandAsync(writer, stream, parts[1], peerIp);
                    }
                    else if (!string.IsNullOrWhiteSpace(command)) // Ignoruj puste linie
                    {
                        Console.WriteLine("Nieznana komenda. Uzyj: 'send <sciezka_do_pliku>' lub 'exit'.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BLAD PETLI GLOWNEJ] {ex.Message}");
            }
            finally
            {
                if (!cts.IsCancellationRequested) cts.Cancel(); // Upewnij sie, ze zadanie nasluchujace zostanie anulowane
                Console.WriteLine("[INFO] Zamykanie polaczenia...");
                // Poczekaj chwile na zakonczenie zadania nasluchujacego
                try { await Task.WhenAny(receiveTask, Task.Delay(1000)); } catch { /* Ignoruj bledy przy czekaniu */}
            }

        } // Koniec using dla client, stream, reader, writer
        Console.WriteLine("[INFO] Polaczenie P2P zakonczone.");
    }

    // --- Wspolna Logika Odbierania Komend ---
    private static async Task ReceivePeerCommandsAsync(StreamReader reader, StreamWriter writer, NetworkStream stream, string peerIp, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[{peerIp}] Rozpoczeto nasluchiwanie na komendy...");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Uzywamy string? dla wyniku ReadLineAsync()
                string? commandLine = await reader.ReadLineAsync();
                if (commandLine == null) // Istniejace sprawdzenie null jest wystarczajace
                {
                    Console.WriteLine($"\n[{peerIp}] Partner zamknal polaczenie (koniec strumienia).");
                    fileAcceptanceTcs?.TrySetResult(false); // Jesli czekalismy, to blad
                    break;
                }

                // Wyswietl prompt ponownie po otrzymaniu wiadomosci
                Console.WriteLine($"\n<-- [{peerIp}] Otrzymano: {commandLine}");
                Console.Write("> Wpisz komende: "); // Ponowne wyswietlenie promptu

                string[] parts = commandLine.Split(SEPARATOR);
                string command = parts[0].ToUpperInvariant();

                switch (command)
                {
                    case CMD_SENDFILE:
                        if (isTransferInProgress)
                        {
                            Console.WriteLine($"[{peerIp}] Otrzymano zadanie SENDFILE, ale inny transfer jest w toku. Odrzucanie.");
                            try { await writer.WriteLineAsync($"{CMD_FILEREJECT}{SEPARATOR}{(parts.Length > 1 ? parts[1] : "nieznany")}{SEPARATOR}Transfer w toku"); } catch { }
                            continue; // Ignoruj zadanie
                        }
                        if (parts.Length == 3 && long.TryParse(parts[2], out long fileSize))
                        {
                            string fileName = Path.GetFileName(parts[1]); // Uzywamy Path.GetFileName dla bezpieczenstwa
                            Console.WriteLine($"[{peerIp}] Zadanie wyslania pliku: {fileName} ({FormatFileSize(fileSize)})");

                            // Automatyczna akceptacja dla uproszczenia
                            string savePath = Path.Combine(DOWNLOAD_DIR, fileName);
                            // Sprawdz czy plik juz istnieje, mozna dodac logike nadpisywania/zmiany nazwy
                            if (File.Exists(savePath))
                            {
                                Console.WriteLine($"Plik '{fileName}' juz istnieje. Nadpisywanie.");
                                // Opcjonalnie: usun lub zmien nazwe przed zapisem
                                // try { File.Delete(savePath); } catch (Exception ex) { Console.WriteLine($"Nie udalo sie usunac istniejacego pliku: {ex.Message}"); continue; }
                            }

                            Console.WriteLine($"Akceptuje plik. Zapisywanie jako: {savePath}");
                            try
                            {
                                await writer.WriteLineAsync($"{CMD_FILEACCEPT}{SEPARATOR}{fileName}");
                                isTransferInProgress = true; // Ustaw flage PRZED rozpoczeciem odbioru
                                bool success = await ReceiveFileAsync(stream, savePath, fileSize, peerIp);
                                isTransferInProgress = false; // Zresetuj flage PO zakonczeniu odbioru
                                if (success) Console.WriteLine($"[{peerIp}] Plik '{fileName}' odebrany pomyslnie.");
                                else Console.WriteLine($"[{peerIp}] Blad podczas odbierania pliku '{fileName}'.");
                                // Usuwanie czesciowego pliku jest teraz w finally w ReceiveFileAsync
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[BLAD] Nie udalo sie wyslac FILEACCEPT lub wystapil blad odbioru: {ex.Message}");
                                isTransferInProgress = false; // Zresetuj flage w razie bledu
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[{peerIp}] Nieprawidlowa komenda SENDFILE.");
                            try { await writer.WriteLineAsync($"{CMD_TRANSFERERROR}{SEPARATOR}Nieprawidlowa komenda SENDFILE"); } catch { }
                        }
                        break;

                    case CMD_FILEACCEPT:
                        // Sprawdzamy czy TCS nie jest null przed uzyciem
                        if (parts.Length >= 2) fileAcceptanceTcs?.TrySetResult(true);
                        else Console.WriteLine($"[{peerIp}] Otrzymano nieprawidlowy FILEACCEPT.");
                        break;

                    case CMD_FILEREJECT:
                        // Sprawdzamy czy TCS nie jest null przed uzyciem
                        if (parts.Length >= 2) fileAcceptanceTcs?.TrySetResult(false);
                        else Console.WriteLine($"[{peerIp}] Otrzymano nieprawidlowy FILEREJECT.");
                        isTransferInProgress = false; // Resetuj flage jesli odrzucono
                        break;

                    case CMD_TRANSFERCOMPLETE:
                        if (parts.Length >= 2) Console.WriteLine($"[{peerIp}] Partner zglosil zakonczenie transferu: {parts[1]}");
                        // isTransferInProgress powinno byc juz false po stronie odbierajacej
                        break;

                    case CMD_TRANSFERERROR:
                        if (parts.Length >= 2) Console.WriteLine($"[{peerIp}] Partner zglosil blad transferu: {string.Join(" ", parts, 1, parts.Length - 1)}");
                        fileAcceptanceTcs?.TrySetResult(false); // Blad oznacza niepowodzenie akceptacji/transferu
                        isTransferInProgress = false; // Zresetuj flage
                        break;

                    case CMD_DISCONNECT:
                        Console.WriteLine($"[{peerIp}] Partner zainicjowal rozlaczenie.");
                        return; // Zakoncz nasluchiwanie

                    default:
                        Console.WriteLine($"[{peerIp}] Nieznana komenda: {commandLine}"); // commandLine nie jest null tutaj
                        break;
                }
            }
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"[{peerIp}] Anulowano nasluchiwanie na komendy (IOException).");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"\n[{peerIp}] Polaczenie zerwane podczas odbierania komend (IOException): {ex.Message}");
            fileAcceptanceTcs?.TrySetResult(false); // Jesli czekalismy, to blad
        }
        catch (ObjectDisposedException)
        {
            Console.WriteLine($"\n[{peerIp}] Strumien polaczenia zamkniety podczas odbierania komend (ObjectDisposed).");
            fileAcceptanceTcs?.TrySetResult(false); // Jesli czekalismy, to blad
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[{peerIp}] Anulowano nasluchiwanie na komendy.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[{peerIp}] BLAD ODBIORU KOMEND: {ex.Message}");
            fileAcceptanceTcs?.TrySetResult(false); // Jesli czekalismy, to blad
        }
        finally
        {
            Console.WriteLine($"[{peerIp}] Zakonczono nasluchiwanie na komendy.");
        }
    }

    // --- Wspolna Logika Wysylania Pliku (Inicjacja) ---
    private static async Task SendFileCommandAsync(StreamWriter writer, NetworkStream stream, string filePath, string peerIp)
    {
        FileInfo? fileInfo = null; // Uzywamy FileInfo?
        try
        {
            filePath = filePath.Trim('"');
            fileInfo = new FileInfo(filePath);
            // Sprawdzamy null jawnie
            if (fileInfo == null || !fileInfo.Exists)
            {
                Console.WriteLine($"[BLAD] Plik nie istnieje lub blad informacji: {filePath}");
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLAD] Nie mozna uzyskac info o pliku {filePath}: {ex.Message}");
            return;
        }

        // Podwojne sprawdzenie flagi
        if (isTransferInProgress)
        {
            Console.WriteLine("[INFO] Blad wewnetrzny: Transfer juz w toku.");
            return;
        }

        isTransferInProgress = true;
        // Utworz nowy TCS dla tego konkretnego zadania akceptacji
        var currentFileAcceptanceTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fileAcceptanceTcs = currentFileAcceptanceTcs; // Przypisz do statycznego pola

        // fileInfo nie jest null w tym miejscu
        string fileName = fileInfo.Name;
        long fileSize = fileInfo.Length;
        bool success = false; // Zmienna do sledzenia sukcesu wyslania danych

        try
        {
            Console.WriteLine($"--> Wysylanie zadania transferu: {fileName} ({FormatFileSize(fileSize)})");
            // Wyslij komende SENDFILE
            await writer.WriteLineAsync($"{CMD_SENDFILE}{SEPARATOR}{fileName}{SEPARATOR}{fileSize}");

            Console.WriteLine("Oczekiwanie na akceptacje partnera...");
            // Czekaj na odpowiedz (FILEACCEPT lub FILEREJECT) od serwera przez max 30 sekund
            var timeoutTask = Task.Delay(30000);
            var completedTask = await Task.WhenAny(currentFileAcceptanceTcs.Task, timeoutTask);

            // Sprawdz czy TCS zostal ustawiony i czy wynik to true
            bool accepted = completedTask == currentFileAcceptanceTcs.Task && currentFileAcceptanceTcs.Task.Result;

            if (!accepted)
            {
                if (completedTask == timeoutTask) Console.WriteLine("[BLAD] Partner nie odpowiedzial na czas.");
                else Console.WriteLine("[INFO] Partner odrzucil plik lub wystapil blad po stronie partnera.");
                // Wyslij blad jesli byl timeout
                if (completedTask == timeoutTask)
                {
                    try { await writer.WriteLineAsync($"{CMD_TRANSFERERROR}{SEPARATOR}{fileName}{SEPARATOR}Timeout akceptacji"); } catch { }
                }
                // isTransferInProgress zostanie zresetowane w finally
                return; // Zakoncz metode jesli nie zaakceptowano
            }

            // --- Wysylanie pliku ---
            Console.WriteLine($"Partner akceptuje plik. Rozpoczynam wysylanie danych...");
            // Przekaz NetworkStream bezposrednio do metody wysylajacej dane
            success = await SendFileAsync(stream, filePath, fileSize, peerIp);

            if (success)
            {
                Console.WriteLine($"\nPlik {fileName} wyslany pomyslnie.");
                // Wyslij komende TRANSFERCOMPLETE
                await writer.WriteLineAsync($"{CMD_TRANSFERCOMPLETE}{SEPARATOR}{fileName}");
            }
            else
            {
                Console.WriteLine($"\n[BLAD] Blad podczas wysylania pliku {fileName}.");
                // Wyslij komende TRANSFERERROR jesli wysylanie danych sie nie powiodlo
                try { await writer.WriteLineAsync($"{CMD_TRANSFERERROR}{SEPARATOR}{fileName}{SEPARATOR}Blad wysylania po stronie nadawcy"); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLAD KRYTYCZNY] Wystapil blad podczas inicjowania wysylania: {ex.ToString()}");
            // Sprobuj wyslac blad do partnera
            try { await writer.WriteLineAsync($"{CMD_TRANSFERERROR}{SEPARATOR}{fileName ?? "nieznany"}{SEPARATOR}Krytyczny blad nadawcy: {ex.Message}"); } catch { }
        }
        finally
        {
            isTransferInProgress = false; // Zawsze resetuj flage po zakonczeniu proby
            fileAcceptanceTcs = null; // Usun referencje do TCS
        }
    }

    // --- Wspolna Logika Wysylania Danych Pliku ---
    private static async Task<bool> SendFileAsync(NetworkStream stream, string filePath, long fileSize, string peerIp)
    {
        long totalBytesSent = 0;
        DateTime lastProgressUpdate = DateTime.MinValue;
        try
        {
            // Uzywamy FileShare.Read dla bezpieczenstwa, jesli plik jest uzywany przez inny proces
            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] buffer = new byte[BUFFER_SIZE];
                int bytesRead;
                while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    // Wysylaj bezposrednio do NetworkStream
                    await stream.WriteAsync(buffer, 0, bytesRead);
                    totalBytesSent += bytesRead;

                    // Raportowanie postepu co sekunde
                    if ((DateTime.Now - lastProgressUpdate).TotalSeconds >= 1)
                    {
                        double percentage = fileSize == 0 ? 100.0 : (double)totalBytesSent * 100 / fileSize; // Unikaj dzielenia przez zero
                        Console.Write($"\r--> Wysylanie do {peerIp}: {percentage:F1}% ({FormatFileSize(totalBytesSent)}/{FormatFileSize(fileSize)})   ");
                        lastProgressUpdate = DateTime.Now;
                    }
                }
                // Koncowy status
                double finalPercentage = fileSize == 0 ? 100.0 : (double)totalBytesSent * 100 / fileSize;
                Console.Write($"\r--> Wysylanie do {peerIp}: {finalPercentage:F1}% ({FormatFileSize(totalBytesSent)}/{FormatFileSize(fileSize)})   ");
            }
            // Sprawdz czy wyslano caly plik
            return totalBytesSent == fileSize;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"\n[BLAD IO] Blad czytania pliku lub wysylania danych {Path.GetFileName(filePath)}: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[BLAD KRYTYCZNY] Wystapil blad podczas wysylania pliku {Path.GetFileName(filePath)}: {ex.ToString()}");
            return false;
        }
    }

    // --- Wspolna Logika Odbierania Danych Pliku ---
    private static async Task<bool> ReceiveFileAsync(NetworkStream stream, string savePath, long fileSize, string peerIp)
    {
        long totalBytesRead = 0;
        DateTime lastProgressUpdate = DateTime.MinValue;
        bool fileStreamCreated = false;
        FileStream? fileStream = null; // Uzywamy FileStream?
        bool success = false; // Flaga sukcesu
        try
        {
            // Utworz strumien pliku
            fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);
            fileStreamCreated = true; // Oznacz, ze strumien zostal utworzony

            using (fileStream) // Uzyj using, aby zapewnic zamkniecie
            {
                byte[] buffer = new byte[BUFFER_SIZE];
                int bytesRead;
                Console.WriteLine($"[{peerIp}] Rozpoczynam odbieranie danych pliku...");
                while (totalBytesRead < fileSize)
                {
                    int bytesToRead = (int)Math.Min(BUFFER_SIZE, fileSize - totalBytesRead);
                    // Czytaj ze strumienia sieciowego
                    bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead);

                    if (bytesRead == 0)
                    {
                        Console.WriteLine($"\n[{peerIp}] BLAD: Strumien zamkniety przedwczesnie. Odebrano {totalBytesRead}/{fileSize} bajtow.");
                        success = false; // Oznacz jako blad
                        return success; // Zwroc od razu
                    }
                    // Zapisz do pliku
                    await fileStream.WriteAsync(buffer, 0, bytesRead);

                    totalBytesRead += bytesRead;
                    // Raportowanie postepu
                    if ((DateTime.Now - lastProgressUpdate).TotalSeconds >= 1)
                    {
                        double percentage = fileSize == 0 ? 100.0 : (double)totalBytesRead * 100 / fileSize;
                        Console.Write($"\r<-- Odbieranie od {peerIp}: {percentage:F1}% ({FormatFileSize(totalBytesRead)}/{FormatFileSize(fileSize)})   ");
                        lastProgressUpdate = DateTime.Now;
                    }
                }
                // Koncowy status
                double finalPercentage = fileSize == 0 ? 100.0 : (double)totalBytesRead * 100 / fileSize;
                Console.Write($"\r<-- Odbieranie od {peerIp}: {finalPercentage:F1}% ({FormatFileSize(totalBytesRead)}/{FormatFileSize(fileSize)})   \n");
            } // fileStream zostanie automatycznie zamkniety przez using

            success = totalBytesRead == fileSize; // Sprawdz czy odebrano calosc
            return success;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"\n[{peerIp}] BLAD IO podczas odbierania/zapisywania pliku {Path.GetFileName(savePath)}: {ex.Message}");
            success = false;
            return success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[{peerIp}] BLAD krytyczny podczas odbierania pliku {Path.GetFileName(savePath)}: {ex.ToString()}");
            success = false;
            return success;
        }
        finally
        {
            // Jesli wystapil blad (success == false) PO utworzeniu pliku, sprobuj usunac czesciowy plik.
            if (fileStreamCreated && !success)
            {
                // Strumien powinien byc juz zamkniety przez 'using', ale dla pewnosci:
                try { fileStream?.Close(); } catch { } // Ignoruj ewentualne bledy przy zamykaniu

                try
                {
                    if (File.Exists(savePath))
                    {
                        Console.WriteLine($"\n[{peerIp}] Proba usuniecia niekompletnego lub blednego pliku: {savePath}");
                        File.Delete(savePath);
                    }
                }
                catch (IOException ioEx) { Console.WriteLine($"\n[{peerIp}] Nie udalo sie usunac pliku {savePath}: {ioEx.Message}"); }
                catch (Exception e) { Console.WriteLine($"\n[{peerIp}] Nieoczekiwany blad podczas usuwania pliku {savePath}: {e.Message}"); }
            }
        }
    }


    // --- Funkcje Pomocnicze ---
    private static string GetIpInput(string prompt, string defaultValue, bool isLocal)
    {
        while (true)
        {
            Console.Write(prompt);
            // Uzywamy string?
            string? input = Console.ReadLine();
            // Uzywamy ?? do zapewnienia wartosci domyslnej (nie null)
            string ipToValidate = string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();

            // TryParse jest bezpieczne pod katem null dla out parametru
            if (IPAddress.TryParse(ipToValidate, out IPAddress? parsedIp)) // Uzywamy IPAddress?
            {
                // Sprawdzamy null przed uzyciem parsedIp
                if (isLocal && parsedIp != null && parsedIp.Equals(IPAddress.Any))
                {
                    // Zwracamy "0.0.0.0" lub pusty input jesli taki byl domyslny i jest poprawny
                    return ipToValidate;
                }
                // Akceptujemy kazdy poprawny format IP, jesli TryParse sie udal
                // Zakladamy, ze jesli TryParse sie udal, to parsedIp nie jest null
                // i zwracamy ipToValidate, ktore tez nie jest null
                if (parsedIp != null)
                {
                    return ipToValidate;
                }
                else
                {
                    // Ten przypadek nie powinien wystapic jesli TryParse zwrocilo true, ale dla bezpieczenstwa
                    Console.WriteLine("Blad parsowania IP. Sprobuj ponownie.");
                }
            }
            else
            {
                Console.WriteLine("Nieprawidlowy format adresu IP. Sprobuj ponownie.");
            }
        }
    }


    private static int GetPortInput(string prompt, int defaultValue)
    {
        while (true)
        {
            Console.Write(prompt);
            // Uzywamy string?
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) return defaultValue;
            // TryParse jest bezpieczne
            if (int.TryParse(input.Trim(), out int port) && port > 0 && port <= 65535)
            {
                return port;
            }
            Console.WriteLine("Nieprawidlowy numer portu. Wpisz liczbe miedzy 1 a 65535.");
        }
    }

    // Bez zmian NRT
    private static string FormatFileSize(long bytes)
    {
        var unit = 1024;
        if (bytes < unit) { return $"{bytes} B"; }
        // Unikaj Log(0) dla plikow 0-bajtowych
        if (bytes == 0) return "0 B";
        var exp = (int)(Math.Log(bytes) / Math.Log(unit));
        // Zapewnij, ze exp jest w zakresie indeksow
        exp = Math.Max(0, Math.Min(exp, "KMGTPE".Length));
        // Jesli exp=0 (dla < 1024), uzyj "B", w przeciwnym razie odpowiedni prefix
        string prefix = exp == 0 ? "B" : ("KMGTPE"[exp - 1] + "B");
        // Jesli exp=0, nie dziel przez unit^exp
        double value = exp == 0 ? bytes : bytes / Math.Pow(unit, exp);
        return $"{value:F2} {prefix}";
    }
}
