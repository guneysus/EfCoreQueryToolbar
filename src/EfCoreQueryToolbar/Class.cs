using EfCoreQueryToolbar;

using HarmonyLib;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class AspnetCoreExtensions
    {
        private static bool _toolbarMiddlewareRegistered;
        private static bool _routeConfigured;

        public static WebApplication UseEfCoreQueryToolbar(this WebApplication app, Action<EfCoreQueryToolbarConfiguration> configAction = default)
        {
            var config = new EfCoreQueryToolbarConfiguration();

            if (configAction != null) configAction(config);

            // Avoid double registration of middleware.
            if (!_toolbarMiddlewareRegistered && config.ToolbarEnabled)
            {
                app.UseMiddleware<EfCoreQueryToolbarMiddleware>();
                _toolbarMiddlewareRegistered = true;
            }

            configureProfilerPage(app, config.ProfilerPageUrl);

            return app;
        }

        static WebApplication configureProfilerPage(this WebApplication app, string route)
        {
            if (_routeConfigured) return app;

            EfCoreQueryToolbarMiddleware.QUERY_LOG_ROUTE = "/" + route;

            QueryLog.ServiceScopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();


            app.MapGet(route, (HttpRequest h) =>
            {
                var metrics = EfCoreMetrics.GetInstance();
                MediaTypeHeaderValue.TryParseList(h.Headers ["Accept"], out var accept);

                IResult resp = accept switch
                {
                    null => QueryLog.TextResult(metrics),
                    var a when a.Any(x => x.MatchesMediaType("text/html")) => QueryLog.HtmlResult(metrics),
                    var a when a.Any(x => x.MatchesMediaType("text/plain")) => QueryLog.TextResult(metrics),
                    var a when a.Any(x => x.MatchesMediaType("application/json")) => QueryLog.JsonResult(metrics),
                    _ => QueryLog.TextResult(metrics)
                };

                return resp;
            });

            app.MapDelete(route, (HttpRequest h) =>
            {
                var metrics = EfCoreMetrics.GetInstance();

                metrics.Clear();
                return Results.NoContent();
            });

            _routeConfigured = true;

            return app;
        }
    }
}

public static class AspnetCoreExtensions
{
    public static IServiceCollection AddEfCoreProfilerToolbar(this IServiceCollection _)
    {
        const string EfCoreRelationalAssemblyString = "Microsoft.EntityFrameworkCore.Relational";
        const string DiagnosticLoggerFullname = "Microsoft.EntityFrameworkCore.Diagnostics.Internal.RelationalCommandDiagnosticsLogger";
        const string DiagnosticLoggerInterfaceName = "IRelationalCommandDiagnosticsLogger";

        var efCoreRelationalAsm = Assembly.Load(EfCoreRelationalAssemblyString);
        ArgumentNullException.ThrowIfNull(efCoreRelationalAsm);

        var h = new Harmony("id");

        Assembly [] assemblies = AppDomain.CurrentDomain.GetAssemblies();

        var diagnosticsLoggerTypes = (
            from t in assemblies.SelectMany(t => t.GetTypes())
            where t.GetInterfaces().Any(t => t.Name.Contains(DiagnosticLoggerInterfaceName))
            select t);

        var efCoreRelationAsm = (
            from asm in assemblies
            where asm.GetName().Name == EfCoreRelationalAssemblyString
            select asm).Single();

        Type [] efCoreRelationalTypes = efCoreRelationAsm.GetTypes();

        var diagnosticsLoggerType = (
            from t in efCoreRelationalTypes
            where t.FullName == DiagnosticLoggerFullname
            select t).Single();

        MethodInfo [] diagnosticsLoggerMethods = diagnosticsLoggerType.GetMethods(AccessTools.all);

        var diagLoggerMethods = (
            from m in diagnosticsLoggerMethods
            where m.Name.Contains("Executed")
            select m);

        patch("CommandReaderExecuted", nameof(Hooks.CommandReaderExecuted), diagLoggerMethods, h);
        patch("CommandReaderExecutedAsync", nameof(Hooks.CommandReaderExecutedAsync), diagLoggerMethods, h);

        patch("CommandScalarExecuted", nameof(Hooks.CommandScalarExecuted), diagLoggerMethods, h);
        patch("CommandScalarExecutedAsync", nameof(Hooks.CommandScalarExecutedAsync), diagLoggerMethods, h);

        patch("CommandNonQueryExecuted", nameof(Hooks.CommandNonQueryExecuted), diagLoggerMethods, h);
        patch("CommandNonQueryExecutedAsync", nameof(Hooks.CommandNonQueryExecutedAsync), diagLoggerMethods, h);

        var relationCommandType = efCoreRelationalTypes.Single(x => x.Name == "RelationalCommand");
        var methods = relationCommandType.GetMethods(AccessTools.all);

        return _;
    }

    private static void patch(string name, string hookName, IEnumerable<MethodInfo> methods, Harmony harmony)
    {
        var replacement = harmony.Patch(methods.Single(x => x.Name == name), new HarmonyMethod(typeof(Hooks).GetMethod(name)));
        ArgumentNullException.ThrowIfNull(replacement);
    }

}

namespace EfCoreQueryToolbar
{
    public struct DashboardData
    {
        public List<MetricSummary> Summaries { get; set; }
        public DashboardData(IDictionary<string, ConcurrentBag<double>> metrics)
        {
            this.Summaries = metrics.Keys.Select(query =>
            {
                var values = metrics [query];

                return new MetricSummary()
                {
                    Query = query,
                    Total = values.Count(),
                    P95 = Math.Round(CalculatePercentile(values.ToArray(), 95), 3)
                };
            }).ToList();

        }

        public static double CalculatePercentile(double [] values, double percentile)
        {
            if (values == null || values.Length == 0)
                throw new ArgumentException("Array cannot be null or empty.");

            if (percentile < 0 || percentile > 100)
                throw new ArgumentException("Percentile must be between 0 and 100.");

            int n = values.Length;
            if (n == 1)
                return values [0];

            // Sort the array in place
            Array.Sort(values);

            // Calculate the rank
            double rank = (percentile / 100.0) * (n - 1);

            // Determine the integer and fractional part of the rank
            int lowerIndex = (int) Math.Floor(rank);
            int upperIndex = (int) Math.Ceiling(rank);

            if (lowerIndex == upperIndex)
                return values [lowerIndex];

            double lowerValue = values [lowerIndex];
            double upperValue = values [upperIndex];

            // Interpolate
            double fraction = rank - lowerIndex;
            return lowerValue + fraction * (upperValue - lowerValue);
        }
    }

    public class EfCoreMetrics
    {
        private static EfCoreMetrics? _instance = null;
        private static readonly object _lock = new object();
        // Private constructor to prevent instantiation
        private EfCoreMetrics()
        {
        }

        public static EfCoreMetrics GetInstance()
        {
            if (_instance != null)
                return _instance;

            // Locking for thread safety
            lock (_lock)
                _instance ??= new EfCoreMetrics();

            return _instance;
        }

        public ConcurrentDictionary<string, ConcurrentBag<double>> Data = new();

        public void Add(Metric metric)
        {
            var item = Data.GetOrAdd(metric.Query.Trim(), new ConcurrentBag<double>());
            item.Add(metric.Duration);
        }

        public void Clear() => Data.Clear();
    }


    public static class QueryLog
    {
        public static IServiceScopeFactory? ServiceScopeFactory { get; internal set; }

        public static void Add(Metric metric)
        {
            EfCoreMetrics instance = EfCoreMetrics.GetInstance();
            instance.Add(metric);
        }

        internal static IResult HtmlResult(EfCoreMetrics metrics)
        {
            // Load header and footer HTML from resources folder
            var pageHtml = EmbeddedResourceHelpers.GetResource("Page.html");

            return Results.Text(pageHtml, "text/html");
        }

        internal static IResult JsonResult(EfCoreMetrics metrics) => Results.Json(new DashboardData(metrics.Data));

        internal static IResult TextResult(EfCoreMetrics metrics) => Results.Text(TextRepr(new DashboardData(metrics.Data)));

        public static string TextRepr(DashboardData summary)
        {
            var sb = new StringBuilder();

            foreach (MetricSummary item in summary.Summaries)
            {
                sb.AppendFormat(@"P95: ""{0}ms"" Total: {1}", item.P95, item.Total)
                    .AppendLine().AppendLine("-")
                    .AppendLine(item.Query)
                    .AppendLine("---").AppendLine();
            }

            string plainText = sb.ToString();
            return plainText;
        }
    }

    public struct MetricSummary
    {
        public string Query { get; set; }
        public long Total { get; set; }

        /// <summary>
        /// TODO: Implement
        /// </summary>
        public double P95 { get; set; }
    }

    public struct Metric
    {
        public double Duration { get; internal set; }
        public string Query { get; internal set; }

#if ENABLE_PARAMETERS_DICT
    public IDictionary<string, object?> Parameters { get; set; }
#endif
    }

    public static class Hooks
    {
        public static void CommandReaderExecuted(object [] __args, TimeSpan duration, object command) => processLog(duration, command);

        public static void CommandScalarExecuted(TimeSpan duration, object command) => processLog(duration, command);

        public static void CommandNonQueryExecuted(TimeSpan duration, object command) => processLog(duration, command);

        public static void CommandReaderExecutedAsync(TimeSpan duration, object command) => processLog(duration, command);

        public static void CommandScalarExecutedAsync(TimeSpan duration, object command) => processLog(duration, command);

        public static void CommandNonQueryExecutedAsync(TimeSpan duration, object command) => processLog(duration, command);

        private static void processLog(TimeSpan duration, object command, [CallerMemberName] string source = default)
        {
            Type type = command.GetType();
            PropertyInfo? cmdTextProp = type.GetProperty("CommandText");

            if (cmdTextProp == null)
#if DEBUG
                throw new ArgumentNullException(nameof(cmdTextProp));
#else
            return; 
#endif

            var cmdText = cmdTextProp.GetValue(command) as string;

            if (cmdText == null)
#if DEBUG
                throw new ArgumentNullException(nameof(cmdText));
#else
            return;
#endif

            var ms = duration.TotalMilliseconds;

            Trace.WriteLine($@"
Query: {cmdText} 
Source: {source}");
            
            var metric = new Metric
            {
                Duration = ms,
                Query = cmdText,
#if ENABLE_PARAMETERS_DICT
                Parameters = parameterValuesDict 
#endif
            };

            QueryLog.Add(metric);
        }
    }

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
        public bool ToolbarEnabled { get; set; } = true;
        public string ProfilerPageUrl { get; set; } = "query-log";
    }

    public class EfCoreQueryToolbarMiddleware
    {
        public static string? QUERY_LOG_ROUTE;

        private readonly RequestDelegate _next;

        public EfCoreQueryToolbarMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!string.IsNullOrEmpty(QUERY_LOG_ROUTE) && context.Request.Path == QUERY_LOG_ROUTE)
            {
                await _next(context);
                return;
            }

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
