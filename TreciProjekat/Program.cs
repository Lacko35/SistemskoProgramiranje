using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using VaderSharp2;

public record GitHubComment(string Body, User User);
public record User(string Login);

//http://localhost:8080/analyze/dotnet/runtime/100000 => URL kojim testiramo rad naseg web servera

public class ReactiveWebServer
{
    private readonly HttpListener _listener = new();
    private readonly HttpClient _githubClient = new();
    private readonly SentimentIntensityAnalyzer _sentimentAnalyzer = new();

    public ReactiveWebServer(string prefix)
    {
        if (!HttpListener.IsSupported)
        {
            Log.Error("HttpListener nije podržan na ovom sistemu.");
            return;
        }
        _listener.Prefixes.Add(prefix);

        _githubClient.BaseAddress = new Uri("https://api.github.com/");
        _githubClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ReactiveGitHubAnalyzer", "1.0"));

        //_githubClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "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");
    }

    public void Start()
    {
        _listener.Start();
        Log.Info($"Server pokrenut i sluša na: {_listener.Prefixes.First()}");
        Log.Info("Primer zahteva: http://localhost:8080/analyze/dotnet/runtime/100000");
        Log.Info("------------------------------------------------------------------");

        IObservable<HttpListenerContext> requestStream = Observable.Create<HttpListenerContext>(async (observer, cancellationToken) =>
        {
            Log.Info("Tok zahteva je kreiran i čeka na konekcije...");
            while (_listener.IsListening && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    observer.OnNext(context);
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995)
                {
                    break;
                }
                catch (Exception ex)
                {
                    observer.OnError(ex);
                    break;
                }
            }
        }).SubscribeOn(NewThreadScheduler.Default);

        requestStream
            .ObserveOn(ThreadPoolScheduler.Instance)
            .Subscribe(
                async context => await ProcessRequest(context),
                error => Log.Error($"Kritična greška u toku zahteva: {error.Message}"),
                () => Log.Info("Server se gasi, tok zahteva je završen.")
            );
    }

    private async Task ProcessRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        Log.Request($"Primljen zahtev: {request.HttpMethod} {request.Url}");

        try
        {
            var pathSegments = request.Url?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (pathSegments?.Length == 4 && pathSegments[0].ToLower() == "analyze")
            {
                var owner = pathSegments[1];
                var repo = pathSegments[2];
                if (!int.TryParse(pathSegments[3], out var issueId))
                {
                    await SendResponse(response, HttpStatusCode.BadRequest, "ID problema (issue) mora biti broj.");
                    return;
                }

                Log.Info($"Započinjem analizu za: {owner}/{repo}, Issue #{issueId}");
                await HandleAnalysisRequest(response, owner, repo, issueId);
            }
            else
            {
                await SendResponse(response, HttpStatusCode.BadRequest, "Format URL-a nije ispravan. Koristite: /analyze/{owner}/{repo}/{issueId}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Greška prilikom obrade zahteva {request.Url}: {ex.Message}");
            await SendResponse(response, HttpStatusCode.InternalServerError, "Došlo je do interne greške na serveru.");
        }
    }

    private async Task HandleAnalysisRequest(HttpListenerResponse response, string owner, string repo, int issueId)
    {
        var analysisStream = Observable.FromAsync(() => GetGitHubCommentsAsync(owner, repo, issueId))
            .SelectMany(comments => comments)
            .Select(comment =>
            {
                var sentiment = _sentimentAnalyzer.PolarityScores(comment.Body);
                return new { Comment = comment, Sentiment = sentiment };
            });

        var results = new StringBuilder("<html><body><h1>Analiza Sentimenta Komentara</h1>");
        results.Append($"<h2>Repozitorijum: {owner}/{repo}, Problem: #{issueId}</h2><hr>");

        await analysisStream.ForEachAsync(result =>
        {
            Log.Info($"Korisnik: {result.Comment.User.Login}, Sentiment: Compound = {result.Sentiment.Compound:F2} ({GetSentimentLabel(result.Sentiment.Compound)})");

            results.Append($"<p><b>Korisnik:</b> {result.Comment.User.Login}<br>");
            results.Append($"<b>Komentar:</b> <i>{WebUtility.HtmlEncode(result.Comment.Body)}</i><br>");
            results.Append($"<b>Sentiment:</b> {GetSentimentLabel(result.Sentiment.Compound)} (Score: {result.Sentiment.Compound:F2})</p><hr>");
        });

        Log.Success($"Analiza za {owner}/{repo} #{issueId} je uspešno završena.");
        results.Append("</body></html>");
        await SendResponse(response, HttpStatusCode.OK, results.ToString());
    }

    private async Task<GitHubComment[]> GetGitHubCommentsAsync(string owner, string repo, int issueId)
    {
        var url = $"repos/{owner}/{repo}/issues/{issueId}/comments";
        Log.Info($"Preuzimanje komentara sa GitHub API: {url}");

        var response = await _githubClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Neuspešan poziv GitHub API-ja. Status: {response.StatusCode}. Proverite da li repozitorijum i problem postoje.");
        }

        var content = await response.Content.ReadAsStringAsync();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var comments = JsonSerializer.Deserialize<GitHubComment[]>(content, options);

        Log.Info($"Pronađeno {comments?.Length ?? 0} komentara.");
        return comments ?? Array.Empty<GitHubComment>();
    }

    private async Task SendResponse(HttpListenerResponse response, HttpStatusCode statusCode, string content)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "text/html; charset=utf-8";
        byte[] buffer = Encoding.UTF8.GetBytes(content);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        response.OutputStream.Close();
        Log.Response($"Odgovor poslat sa statusom: {statusCode}");
    }

    private string GetSentimentLabel(double compoundScore)
    {
        if (compoundScore >= 0.05) return "Pozitivan";
        if (compoundScore <= -0.05) return "Negativan";
        return "Neutralan";
    }

    public void Stop()
    {
        Log.Info("Server se zaustavlja...");
        _listener.Stop();
        _listener.Close();
    }
}

public static class Log
{
    private static void Write(string message, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        Console.ResetColor();
    }

    public static void Info(string message) => Write(message, ConsoleColor.White);
    public static void Request(string message) => Write(message, ConsoleColor.Cyan);
    public static void Response(string message) => Write(message, ConsoleColor.Blue);
    public static void Success(string message) => Write(message, ConsoleColor.Green);
    public static void Error(string message) => Write(message, ConsoleColor.Red);
}

class Program
{
    static void Main(string[] args)
    {
        var server = new ReactiveWebServer("http://localhost:8080/");
        server.Start();

        Console.WriteLine("Pritisnite Enter za zaustavljanje servera...");
        Console.ReadLine();

        server.Stop();
    }
}