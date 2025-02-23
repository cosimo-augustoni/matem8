using System.Globalization;
using System.Reflection;
using MateM8.ApiService;
using Microsoft.EntityFrameworkCore;
using SendGrid;
using SendGrid.Helpers.Mail;

public class InvoicingService(IServiceProvider serviceProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var next = new DateTime(now.Year, now.Month, now.Day, now.Hour + 1, 0, 0);
            await Task.Delay(next - now, stoppingToken);

            if (DateTime.Now.Day == 1 && DateTime.Now.Hour == 6)
            {
                try
                {
                    await SendInvoicesAsync();
                }
                catch (Exception e)
                {
                    Console.WriteLine("Sending Invoices failed:" + e);
                }
                
            }
        }
    }

    private async Task SendInvoicesAsync()
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MateDbContext>();

        var beginOfLastMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month - 1, 1);
        var endOfLastMonth = beginOfLastMonth.AddMonths(1);
        var monthText = beginOfLastMonth.ToString("MMMM", new CultureInfo("de-CH"));

        var matesPerUserLastMonth =
            await dbContext.Mates
                .Where(m => m.CreatedAt >= beginOfLastMonth && m.CreatedAt < endOfLastMonth)
                .GroupBy(m => m.User)
                .ToListAsync();

        var mailSettings = configuration.GetRequiredSection("Mail").Get<MailSettings>();
        var mailClient = new SendGridClient(mailSettings.ApiKey);

        foreach (var matesPerUser in matesPerUserLastMonth)
        {
            var email = matesPerUser.Key;

            string mailbody;

            var assembly = Assembly.GetExecutingAssembly();
            var emailTemplateResourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith("invoice.html"));
            await using (var emailTemplateResource = assembly.GetManifestResourceStream(emailTemplateResourceName))
            using (var reader = new StreamReader(emailTemplateResource!))
            {
                mailbody = await reader.ReadToEndAsync();
                mailbody = mailbody.Replace("??Month??", monthText);
                var classicCount = matesPerUser.Count(m => m.Type == MateType.Blue);
                mailbody = mailbody.Replace("??ClassicCount??", classicCount.ToString());
                mailbody = mailbody.Replace("??ClassicAmount??", (classicCount * 1.67m).ToString("C"));

                var gingerCount = matesPerUser.Count(m => m.Type == MateType.Ginger);
                mailbody = mailbody.Replace("??GingerCount??", gingerCount.ToString());
                mailbody = mailbody.Replace("??GingerAmount??", (gingerCount * 1.67m).ToString("C"));

                var mintCount = matesPerUser.Count(m => m.Type == MateType.Mint);
                mailbody = mailbody.Replace("??MintCount??", mintCount.ToString());
                mailbody = mailbody.Replace("??MintAmount??", (mintCount * 1.67m).ToString("C"));

                var totalCount = matesPerUser.Count();
                mailbody = mailbody.Replace("??TotalCount??", totalCount.ToString());
                mailbody = mailbody.Replace("??TotalAmount??", (totalCount * 1.67m).ToString("C"));

            }

            var from = new EmailAddress(mailSettings.InvoiceFrom, "Mate M8");
            var mailMessage = MailHelper.CreateSingleEmail(from, new EmailAddress(email), $"Mate M8 Rechnung {monthText}", null, mailbody);
            await mailClient.SendEmailAsync(mailMessage);
        }
    }
}