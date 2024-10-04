using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PrintService
{
    class Program
    {
        static async Task Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    services.AddHostedService<Worker>();
                    services.AddSingleton<PdfDownloader>();
                    services.AddLogging(configure => configure.AddConsole());
                });
    }
}
