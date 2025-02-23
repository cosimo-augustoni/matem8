using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MateM8.ApiService;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using SendGrid;
using SendGrid.Helpers.Mail;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("./config/appsettings.json", optional: false);

builder.Host.ConfigureLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole();
});

builder.Services.AddProblemDetails();

builder.Services.AddDbContext<MateDbContext>();

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor
});

app.UseExceptionHandler();

app.Map("/health", () => Results.Ok());

app.Map("/", (HttpContext context) =>
{
    if (context.Request.Cookies.ContainsKey("session"))
        return Results.LocalRedirect("/mate.html");

    return Results.LocalRedirect("/signup.html");
});

app.MapPost("/email", async (HttpContext context, MateDbContext dbContext, IConfiguration configuration) =>
{
    var formData = await context.Request.ReadFormAsync();
    var email = formData["email"].ToString();

    var otpBytes = new byte[4];
    RandomNumberGenerator.Create().GetBytes(otpBytes);
    var otp = ((BitConverter.ToInt32(otpBytes) & int.MaxValue) % 100000000).ToString("00000000");

    var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Email == email);
    if (user != null)
    {
        user.Otp = otp;
        user.Session = null;
    }
    else
    {
        var newUser = new User
        {
            Email = email,
            Otp = otp,
            Session = null
        };
        await dbContext.Users.AddAsync(newUser);
    }

    await dbContext.SaveChangesAsync();



    string mailbody;

    var assembly = Assembly.GetExecutingAssembly();
    var emailTemplateResourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith("singup_email.html"));
    await using (var emailTemplateResource = assembly.GetManifestResourceStream(emailTemplateResourceName))
    using (var reader = new StreamReader(emailTemplateResource!))
    {
        mailbody = reader.ReadToEnd();
        mailbody = mailbody.Replace("??ApplicationUrl??",
            $"{configuration.GetValue<string>("ApplicationUrl")}/confirm/{Convert.ToBase64String(Encoding.UTF8.GetBytes(email))}/{otp}");
    }

    var mailSettings = configuration.GetRequiredSection("Mail").Get<MailSettings>();

    var mailClient = new SendGridClient(mailSettings.ApiKey);

    var from = new EmailAddress(mailSettings.From, "Mate M8");
    var mailMessage = MailHelper.CreateSingleEmail(from, new EmailAddress(email), "Mate M8 Anmeldung", null, mailbody);
    await mailClient.SendEmailAsync(mailMessage);

    return Results.LocalRedirect("/email_sent.html");
});

app.MapGet("/confirm/{emailHash}/{otp}", async (string emailHash, string otp, HttpContext context, MateDbContext dbContext, IConfiguration configuration) =>
{
    var users = await dbContext.Users.Where(u => u.Otp != null).ToListAsync();
    var user = users.FirstOrDefault(u => Convert.ToBase64String(Encoding.UTF8.GetBytes(u.Email)) == emailHash);
    if (user == null || user.Otp != otp)
        return Results.LocalRedirect("/");

    var hashKey = configuration.GetValue<string>("SessionHashKey");
    using var hmacsha256 = new HMACSHA256(Encoding.UTF8.GetBytes(hashKey));
    var otpHash = hmacsha256.ComputeHash(Encoding.UTF8.GetBytes(otp));
    var session = Convert.ToBase64String(otpHash);

    user.Otp = null;
    user.Session = session;
    await dbContext.SaveChangesAsync();

    var cookieOptions = new CookieOptions()
    {
        Expires = DateTimeOffset.UtcNow.AddMonths(3),
        IsEssential = true,
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,

    };
    context.Response.Cookies.Append("session", session, cookieOptions);
    return Results.LocalRedirect("/mate.html");
});

app.MapPut("/mate/{mateType}/{count:int}", async (MateType mateType, int count, HttpContext context, MateDbContext dbContext) =>
{
    if (!context.Request.Cookies.TryGetValue("session", out var session))
    {
        context.Response.Cookies.Append("session", "invalid-Session", new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddDays(-1),
            IsEssential = true,
            HttpOnly = true,
            Secure = true,
        });
        return Results.Redirect("/session_ended.html");
    }

    var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Session == session);
    if (user == null)
    {
        context.Response.Cookies.Append("session", "invalid-Session", new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddDays(-1),
            IsEssential = true,
            HttpOnly = true,
            Secure = true,
        });
        return Results.Redirect("/session_ended.html");
    }


    for (int i = 0; i < count; i++)
    {
        var mate = new Mate()
        {
            Id = 0,
            CreatedAt = DateTimeOffset.Now,
            User = user.Email,
            Type = mateType
        };
        await dbContext.Mates.AddAsync(mate);
    }
    await dbContext.SaveChangesAsync();

    return Results.LocalRedirect("/mate_recorded.html");
});

app.UseFileServer(new FileServerOptions
{
    StaticFileOptions = {
        OnPrepareResponse = ctx =>
        {
            if (ctx.Context.Request.Path.Value.EndsWith("mate.html") && !ctx.Context.Request.Cookies.ContainsKey("session"))
            {
                ctx.Context.Response.Redirect("/");
            }
        }
    }
});

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<MateDbContext>();
    dbContext.Database.Migrate();
}

app.Run();