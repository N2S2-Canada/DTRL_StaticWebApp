using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using MimeKit;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;

namespace API.Functions;

public class SendContact
{
    private readonly ILogger<SendContact> _logger;
    public SendContact(ILogger<SendContact> logger) => _logger = logger;

    private sealed class ContactForm
    {
        [Required, StringLength(100)] public string? Name { get; set; }
        [Required, EmailAddress, StringLength(200)] public string? Email { get; set; }
        [Required, StringLength(4000)] public string? Message { get; set; }
        public string? Honeypot { get; set; } // bot trap
    }

    [Function("SendContact")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "contact")] HttpRequestData req)
    {
        var res = req.CreateResponse();

        try
        {
            // ---------- Parse & validate ----------
            var json = await new StreamReader(req.Body).ReadToEndAsync();
            var data = JsonSerializer.Deserialize<ContactForm>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data is null)
            {
                res.StatusCode = HttpStatusCode.BadRequest;
                await res.WriteStringAsync("Invalid payload.");
                return res;
            }

            // Honeypot: silently succeed
            if (!string.IsNullOrWhiteSpace(data.Honeypot))
            {
                res.StatusCode = HttpStatusCode.OK;
                await res.WriteStringAsync("OK");
                return res;
            }

            var ctx = new ValidationContext(data);
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(data, ctx, results, true))
            {
                res.StatusCode = HttpStatusCode.BadRequest;
                await res.WriteStringAsync(string.Join("; ", results.Select(r => r.ErrorMessage)));
                return res;
            }

            var name = data.Name!.Trim();
            var email = data.Email!.Trim();
            var message = data.Message!.Trim();
            if (message.Length < 5)
            {
                res.StatusCode = HttpStatusCode.BadRequest;
                await res.WriteStringAsync("Message is too short.");
                return res;
            }

            // ---------- Config (env vars) ----------
            // For Zoho US: smtp.zoho.com; for Zoho EU: smtp.zoho.eu
            var smtpHost = Environment.GetEnvironmentVariable("ZOHO_SMTP_HOST") ?? "smtp.zoho.com";
            var smtpPortStr = Environment.GetEnvironmentVariable("ZOHO_SMTP_PORT") ?? "465"; // 587=STARTTLS, 465=SSL
            var smtpPort = int.TryParse(smtpPortStr, out var p) ? p : 465;

            var zohoUser = Environment.GetEnvironmentVariable("ZOHO_USERNAME");       // full address, e.g. you@yourdomain.com
            var zohoAppPass = Environment.GetEnvironmentVariable("ZOHO_APP_PASSWORD");   // Zoho app password
            var toEmail = Environment.GetEnvironmentVariable("CONTACT_TO_EMAIL")
                                ?? "info@diversetechrobotics.ca";
            var fromName = Environment.GetEnvironmentVariable("CONTACT_FROM_NAME")
                                ?? "DiverseTech Robotics Ltd. (DTRL)";

            if (string.IsNullOrWhiteSpace(zohoUser) || string.IsNullOrWhiteSpace(zohoAppPass))
            {
                _logger.LogError("Missing Zoho SMTP settings (ZOHO_USERNAME / ZOHO_APP_PASSWORD).");
                res.StatusCode = HttpStatusCode.InternalServerError;
                await res.WriteStringAsync("Email is not configured.");
                return res;
            }

            // ---------- Build email ----------
            string HtmlEncode(string s) => WebUtility.HtmlEncode(s);

            var subject = $"📨 New Contact Form: {name}";
            var plain = new StringBuilder()
                .AppendLine("New contact form submission:")
                .AppendLine($"Name: {name}")
                .AppendLine($"Email: {email}")
                .AppendLine()
                .AppendLine("Message:")
                .AppendLine(message)
                .ToString();

            var html = $@"
<div style=""font-family:Segoe UI,Arial,sans-serif;line-height:1.5"">
  <h2>New contact form submission</h2>
  <p><strong>Name:</strong> {HtmlEncode(name)}<br/>
     <strong>Email:</strong> {HtmlEncode(email)}</p>
  <p><strong>Message:</strong><br/>{HtmlEncode(message).Replace("\n", "<br/>")}</p>
</div>";

            var mime = new MimeMessage();
            // From must match your Zoho mailbox (the authenticated user)
            mime.From.Add(new MailboxAddress(fromName, zohoUser));
            mime.To.Add(new MailboxAddress("DiverseTech Robotics", toEmail));
            mime.ReplyTo.Add(new MailboxAddress(name, email)); // replies go to visitor
            mime.Subject = subject;

            var builder = new BodyBuilder { TextBody = plain, HtmlBody = html };
            mime.Body = builder.ToMessageBody();

            // ---------- Send via Zoho SMTP ----------
            using var client = new SmtpClient();
            client.Timeout = 10_000; // 10s

            // Use SSL for 465, STARTTLS for 587
            var secure = smtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(smtpHost, smtpPort, secure);
            await client.AuthenticateAsync(zohoUser, zohoAppPass);
            await client.SendAsync(mime);
            await client.DisconnectAsync(true);

            res.StatusCode = HttpStatusCode.OK;
            await res.WriteStringAsync("OK");
            return res;
        }
        catch (SmtpCommandException ex)
        {
            _logger.LogError(ex, "SMTP command error: {Status} {Message}", ex.StatusCode, ex.Message);
            var r = req.CreateResponse(HttpStatusCode.BadGateway);
            await r.WriteStringAsync("Failed to send email.");
            return r;
        }
        catch (SmtpProtocolException ex)
        {
            _logger.LogError(ex, "SMTP protocol error.");
            var r = req.CreateResponse(HttpStatusCode.BadGateway);
            await r.WriteStringAsync("Failed to send email.");
            return r;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error sending email.");
            var r = req.CreateResponse(HttpStatusCode.InternalServerError);
            await r.WriteStringAsync("Error.");
            return r;
        }
    }
}
