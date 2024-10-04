using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfiumViewer;
using Supabase;
using WebSocketSharp;
using System.Text.Json.Serialization;

namespace PrintService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly BlockingCollection<PdfDownloader.PrintSettings> _printQueue = new BlockingCollection<PdfDownloader.PrintSettings>(10);
        private readonly string _printerName = "HP LaserJet 1018"; // Change this to the actual printer name
        private readonly PdfDownloader _pdfDownloader;
        private WebSocket _webSocketClient;
        private string _shopId;

        public Worker(ILogger<Worker> logger, PdfDownloader pdfDownloader)
        {
            _logger = logger;
            _pdfDownloader = pdfDownloader;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _shopId = "16"; // Get the shop_id dynamically (can be fetched from settings, environment variable, etc.)

            InitializeWebSocket(_shopId);

            var monitoringTask = MonitorWebSocketForJobs(stoppingToken);
            var processingTask = ProcessPrintQueue(stoppingToken);

            await Task.WhenAll(monitoringTask, processingTask);
        }

        private void InitializeWebSocket(string shopId)
        {
            string webSocketUrl = $"ws://localhost:8080/{shopId}";
            _webSocketClient = new WebSocket(webSocketUrl);
            
            _webSocketClient.OnOpen += (sender, e) => {
                _logger.LogInformation($"WebSocket connection established for shop {shopId}.");
            };

            _webSocketClient.OnMessage += async (sender, e) => {
                _logger.LogInformation($"Received message from WebSocket server: {e.Data}");

                try
                {
                    var messageData = JsonSerializer.Deserialize<WebSocketMessage>(e.Data, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    _logger.LogInformation($"Deserialized message: {JsonSerializer.Serialize(messageData)}");

                    if (messageData?.Type == "NEW_JOBS" && messageData.Jobs != null)
                    {
                        _logger.LogInformation($"Received {messageData.Jobs.Count} new job(s)");
                        foreach (var job in messageData.Jobs)
                        {
                            _logger.LogInformation($"Processing job: {JsonSerializer.Serialize(job)}");
                            await _pdfDownloader.DownloadFileFromSupabase(job.File);
                            _printQueue.Add(job);
                            _logger.LogInformation($"Added job {job.JobId} to print queue");
                        }
                        
                        var jobIds = messageData.Jobs.Select(j => j.JobId).ToList();
                        var jobUpdate = new { type = "JOB_RECEIVED", job_ids = jobIds };
                        _webSocketClient.Send(JsonSerializer.Serialize(jobUpdate));
                        _logger.LogInformation($"Sent job update: {string.Join(", ", jobIds)} - Assigned to print queue.");
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError($"Error deserializing WebSocket message: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error processing WebSocket message: {ex.Message}");
                }
            };

            _webSocketClient.OnError += (sender, e) => {
                _logger.LogError($"WebSocket error: {e.Message}");
            };

            _webSocketClient.OnClose += (sender, e) => {
                _logger.LogInformation($"WebSocket connection closed for shop {shopId}.");
            };

            _webSocketClient.Connect();
        }

        private async Task MonitorWebSocketForJobs(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_webSocketClient.ReadyState != WebSocketState.Open)
                {
                    _logger.LogError("WebSocket connection lost. Reconnecting...");
                    _webSocketClient.Connect();
                }

                await Task.Delay(4000, stoppingToken);
            }

            _printQueue.CompleteAdding();
        }

        private async Task ProcessPrintQueue(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting to process print queue");
            foreach (var printJob in _printQueue.GetConsumingEnumerable(stoppingToken))
            {
                _logger.LogInformation($"Processing print job: {JsonSerializer.Serialize(printJob)}");
                try
                {
                    string filePath = Path.Combine(_pdfDownloader.DownloadFolderPath, printJob.File);
                    _logger.LogInformation($"Attempting to print file: {filePath}");
                    
                    if (!File.Exists(filePath))
                    {
                        _logger.LogError($"File not found: {filePath}. Attempting to download again.");
                        await _pdfDownloader.DownloadFileFromSupabase(printJob.File);
                    }

                    using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                    {
                        await PrintFile(fileStream, printJob);

                        var jobUpdate = new { type = "JOB_COMPLETED", job_ids = new[] { printJob.JobId } };
                        _webSocketClient.Send(JsonSerializer.Serialize(jobUpdate));
                        _logger.LogInformation($"Sent job update: {printJob.File} - Printed");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error processing file {printJob.File}: {ex.Message}");
                    var jobUpdate = new { type = "JOB_FAILED", job_ids = new[] { printJob.JobId } };
                    _webSocketClient.Send(JsonSerializer.Serialize(jobUpdate));
                }

                await Task.Delay(1000, stoppingToken);
            }
        }

private async Task PrintFile(Stream fileStream, PdfDownloader.PrintSettings fileInfo)
{
    try
    {
        _logger.LogInformation($"Starting to print file: {fileInfo.File}");

        using (var pdfDocument = PdfiumViewer.PdfDocument.Load(fileStream))
        {
            // Track the current page
            int currentPage = fileInfo.StartPage - 1; // Page index is zero-based

            // Create a PrintDocument
            var printDocument = new System.Drawing.Printing.PrintDocument
            {
                DocumentName = fileInfo.File,
                PrinterSettings =
                {
                    PrinterName = _printerName,
                    Copies = (short)fileInfo.Copies,
                    Duplex = fileInfo.Duplex switch
                    {
                        "horizontal" => Duplex.Horizontal,
                        "vertical" => Duplex.Vertical,
                        _ => Duplex.Simplex
                    }
                },
                DefaultPageSettings =
                {
                    Landscape = fileInfo.Orientation.ToLower() == "landscape",
                    Margins = new Margins(0, 0, 0, 0)  // Set margins to zero
                }
            };

            // Set paper size
            foreach (PaperSize size in printDocument.PrinterSettings.PaperSizes)
            {
                if (size.PaperName == fileInfo.PaperSize)
                {
                    printDocument.DefaultPageSettings.PaperSize = size;
                    break;
                }
            }

            // Handle PrintPage event
            printDocument.PrintPage += (sender, args) =>
            {
                if (currentPage < pdfDocument.PageCount && currentPage < fileInfo.EndPage)
                {
                    // Render the PDF page at a high DPI (600 DPI)
                    int dpi = 600;
                    var page = pdfDocument.Render(currentPage, dpi, dpi, true);

                    // Calculate scaling to fit the image to the entire printable area
                    var scaleX = args.PageBounds.Width / (float)page.Width;
                    var scaleY = args.PageBounds.Height / (float)page.Height;
                    var scale = Math.Min(scaleX, scaleY);

                    // Calculate the dimensions of the scaled image
                    var destWidth = (int)(page.Width * scale);
                    var destHeight = (int)(page.Height * scale);

                    // Draw the image to fill the entire page
                    args.Graphics.DrawImage(page, 
                                            0,  // X position at the left edge
                                            0,  // Y position at the top edge
                                            destWidth,  // Width of the image
                                            destHeight); // Height of the image

                    page.Dispose();

                    // Update the current page index
                    currentPage++;
                    args.HasMorePages = (currentPage < pdfDocument.PageCount && currentPage < fileInfo.EndPage);
                }
                else
                {
                    // No more pages
                    args.HasMorePages = false;
                }
            };

            // Print the document
            printDocument.Print();

            _logger.LogInformation($"Printed file: {fileInfo.File}");
        }
    }
    catch (Exception ex)
    {
        _logger.LogError($"Error printing file {fileInfo.File}: {ex.Message}");
        throw;  // Re-throw the exception to be caught in the calling method
    }
}
}

    public class WebSocketMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("jobs")]
    public List<PdfDownloader.PrintSettings> Jobs { get; set; }
}
   public class PdfDownloader
{
    private readonly ILogger<PdfDownloader> _logger;
    private readonly string _supabaseUrl = "https://gxbdltjmtvrtsomvbrgv.supabase.co";
    private readonly string _supabaseKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Imd4YmRsdGptdHZydHNvbXZicmd2Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3MjI3NzA1MzMsImV4cCI6MjAzODM0NjUzM30.pUmy3OF_GeMPxQ0L9ysFgya44SZYybXBe4V8avM_5s0";
    private readonly string _bucketName_user = "print-pdf";
    private readonly string _bucketName_store = "print-store";
    public string DownloadFolderPath { get; } = "C:\\Users\\prakh\\Downloads";
    public Supabase.Client SupabaseClient { get; private set; }

    public PdfDownloader(ILogger<PdfDownloader> logger)
    {
        _logger = logger;
        InitializeSupabaseClient();
    }

    private void InitializeSupabaseClient()
    {
        try
        {
            var options = new Supabase.SupabaseOptions
            {
                AutoConnectRealtime = true
            };
            SupabaseClient = new Supabase.Client(_supabaseUrl, _supabaseKey, options);
            SupabaseClient.InitializeAsync().Wait();
            _logger.LogInformation("Supabase client initialized successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error initializing Supabase client: {ex.Message}");
            throw;
        }
    }

    public async Task DownloadFileFromSupabase(string fileName)
{
    _logger.LogInformation($"Attempting to download file: {fileName}");
    string bucketName ="print-pdf" ;
    string fullFileUrl = $"{_supabaseUrl}/storage/v1/object/public/{bucketName}/{fileName}";

    try
    {
        using (var httpClient = new HttpClient())
        {
            httpClient.Timeout = TimeSpan.FromMinutes(5); // Set a reasonable timeout

            using (var response = await httpClient.GetAsync(fullFileUrl))
            {
                if (response.IsSuccessStatusCode)
                {
                    string filePath = Path.Combine(DownloadFolderPath, fileName);
                    using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                    {
                        await response.Content.CopyToAsync(fileStream);
                    }

                    _logger.LogInformation($"File downloaded successfully: {fileName}");
                }
                else
                {
                    _logger.LogError($"Failed to download file: {fileName}, status code: {response.StatusCode}");
                    throw new HttpRequestException($"HTTP request failed with status code: {response.StatusCode}");
                }
            }
        }
    }
    catch (HttpRequestException ex)
    {
        _logger.LogError($"HTTP request error while downloading file {fileName}: {ex.Message}");
        throw;
    }
    catch (TaskCanceledException ex)
    {
        _logger.LogError($"Download operation timed out for file {fileName}: {ex.Message}");
        throw;
    }
    catch (Exception ex)
    {
        _logger.LogError($"Unexpected error downloading file {fileName} from Supabase: {ex.Message}");
        throw;
    }
}


     
        
        public class PrintSettings
        {
            [JsonPropertyName("job_id")]
            public int JobId { get; set; }

            [JsonPropertyName("size")]
            public int Size { get; set; }

            [JsonPropertyName("copies")]
            public int Copies { get; set; }

            [JsonPropertyName("start_page")]
            public int StartPage { get; set; }

            [JsonPropertyName("end_page")]
            public int EndPage { get; set; }

            [JsonPropertyName("file")]
            public string File { get; set; }

            [JsonPropertyName("color_mode")]
            public string ColorMode { get; set; }

            [JsonPropertyName("orientation")]
            public string Orientation { get; set; }

            [JsonPropertyName("paper_size")]
            public string PaperSize { get; set; }

            [JsonPropertyName("duplex")]
            public string Duplex { get; set; }

        }
    
    }
}

