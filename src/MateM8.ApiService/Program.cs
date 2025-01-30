using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using MateM8.ApiService;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

builder.Services.AddDbContext<MateDbContext>();

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor
});

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

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

    var smtpSettings = configuration.GetRequiredSection("Smtp").Get<SmtpSettings>();

    var client = new SmtpClient(smtpSettings.Host, 587)
    {
        EnableSsl = true,
        UseDefaultCredentials = false,
        Credentials = new NetworkCredential(smtpSettings.Username, smtpSettings.Password)
    };

    var mailMessage = new MailMessage(from: "signIn@mate.kallisto.li",
        to: email,
        "OTP zum einloggen",
        $"<a href=\"{configuration.GetValue<string>("ApplicationUrl")}/confirm/{email}/{otp}\">Einloggen</a>"
    );
    mailMessage.IsBodyHtml = true;
    await client.SendMailAsync(mailMessage);

    return Results.Ok();
});

app.MapGet("/confirm/{email}/{otp}", async (string email, string otp, HttpContext context, MateDbContext dbContext, IConfiguration configuration) =>
{
    var user = await dbContext.Users.FirstAsync(u => u.Email == email);
    if (user.Otp != otp)
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
    };
    context.Response.Cookies.Append("session", session, cookieOptions);
    return Results.LocalRedirect("/mate.html");
});

app.MapPut("/mate", async (HttpContext context, MateDbContext dbContext) =>
{
    if (!context.Request.Cookies.TryGetValue("session", out var session))
        return Results.Redirect("/");
    var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Session == session);
    if (user == null)
        return Results.Redirect("/");

    var mate = new Mate()
    {
        Id = 0,
        User = user.Email,
    };
    await dbContext.Mates.AddAsync(mate);
    await dbContext.SaveChangesAsync();

    return Results.Ok();
});

app.MapDefaultEndpoints();

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

public class SmtpSettings
{
    public required string Host { get; set; }
    public required string Username { get; set; }
    public required string Password { get; set; }
}

