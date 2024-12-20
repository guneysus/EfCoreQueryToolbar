using EfCoreQueryToolbar;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class AspnetCoreExtensions
    {
        private static bool _toolbarMiddlewareRegistered;

        public static WebApplication UseEfCoreQueryToolbar(this WebApplication app, Action<EfCoreQueryToolbarConfiguration> config = default)
        {

            // Avoid double registration of middleware.
            if (!_toolbarMiddlewareRegistered)
            {
                app.UseMiddleware<EfCoreQueryToolbarMiddleware>();
                _toolbarMiddlewareRegistered = true;
            }

            return app;
        }
    }
}

namespace EfCoreQueryToolbar
{
    internal static class EmbeddedResourceHelpers
    {

        public static string GetResource(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceFullname = $"EfCoreQueryToolbar.Resources.{resourceName}";
            using var stream = assembly.GetManifestResourceStream(resourceFullname);

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            return content;
        }
    }

    public class EfCoreQueryToolbarConfiguration
    {
    }

    public class EfCoreQueryToolbarMiddleware
    {
        private readonly RequestDelegate _next;
        
        public EfCoreQueryToolbarMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var originalBodyStream = context.Response.Body;
            await using var newBodyStream = new MemoryStream();
            context.Response.Body = newBodyStream;

            try
            {
                // Call the next middleware
                await _next(context);

                // If the response is HTML, modify it
                if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                {
                    newBodyStream.Seek(0, SeekOrigin.Begin);
                    var body = await new StreamReader(newBodyStream).ReadToEndAsync();

                    var toolbarHtml = EmbeddedResourceHelpers.GetResource("Toolbar.html");

                    if (body.Contains("</body>", StringComparison.OrdinalIgnoreCase))
                    {
                        body = body.Replace("</body>", $"{toolbarHtml}</body>", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        body += toolbarHtml;
                    }

                    var bodyBytes = Encoding.UTF8.GetBytes(body);

                    // Reset the response body
                    context.Response.ContentLength = bodyBytes.Length;
                    newBodyStream.SetLength(0);
                    await newBodyStream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
                    await newBodyStream.FlushAsync();
                    newBodyStream.Seek(0, SeekOrigin.Begin);
                }

                // Copy the modified or original response body back to the original stream
                newBodyStream.Seek(0, SeekOrigin.Begin);
                await newBodyStream.CopyToAsync(originalBodyStream);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error in QueryLogMiddleware: {ex}");
                throw;
            }
            finally
            {
                context.Response.Body = originalBodyStream;
            }

        }
    }
}
