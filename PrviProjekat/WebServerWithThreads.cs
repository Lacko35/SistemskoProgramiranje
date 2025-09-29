using System;
using System.Net;
using System.Threading;
using System.Collections.Generic;
using System.Net.Http;
using System.Diagnostics;

public class WebServerWithThreads
{
    private readonly HttpListener listenerObj = new HttpListener();
    
    private readonly string apiBaseUrl = "http://api.weatherapi.com/v1/forecast.json";
    private readonly string apiKey = "73cc3100896e493d868111304252509";
    
    private readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
    private static readonly object cacheLock = new object();
    
    private static readonly HttpClient httpKlijent = new HttpClient();
    private static readonly object logLock = new object();

    public WebServerWithThreads(string prefix)
    {
        if (!HttpListener.IsSupported)
        {
            Log("HttpListener nije podržan na ovom sistemu.", ConsoleColor.Red);
            return;
        }
        listenerObj.Prefixes.Add(prefix);
    }

    public void Start()
    {
        Thread listenerThread = new Thread(() =>
        {
            listenerObj.Start();
            Log($"Web server pokrenut na adresi: {listenerObj.Prefixes.First()}", ConsoleColor.Green);
            Log("Server čeka na zahteve...", ConsoleColor.White);

            try
            {
                while (listenerObj.IsListening)
                {
                    var context = listenerObj.GetContext();

                    Thread requestThread = new Thread(() =>
                    {
                        try
                        {
                            ProcessRequest(context);
                        }
                        catch (Exception ex)
                        {
                            Log($"Greška u obradi zahteva: {ex.Message}", ConsoleColor.Yellow);
                        }
                    });
                    requestThread.Start();
                }
            }
            catch (HttpListenerException) { /* Listener je zatvoren */ }
            catch (Exception ex)
            {
                Log($"Greška servera: {ex.Message}", ConsoleColor.Red);
            }
        });

        listenerThread.IsBackground = true; // da ne blokira izlazak iz main-a
        listenerThread.Start();
    }

    private void ProcessRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        string requestUrl = request.RawUrl ?? "/";
        var stopwatch = Stopwatch.StartNew();

        if (requestUrl.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = (int)HttpStatusCode.NoContent;
            response.OutputStream.Close();
            return;
        }

        Log($"Primljen zahtev: {request.HttpMethod} {requestUrl}", ConsoleColor.Cyan);

        try
        {
            string result;

            lock (cacheLock)
            {
                if (_cache.TryGetValue(requestUrl, out string cached))
                {
                    result = cached;
                    Log($"[CACHE HIT] {requestUrl}", ConsoleColor.Yellow);
                }
                else
                {
                    Log($"[CACHE MISS] Nema odgovora u kešu za: {requestUrl}", ConsoleColor.Gray);

                    string query = request.Url?.Query.Replace("?", "&") ?? string.Empty;
                    string apiUrl = $"{apiBaseUrl}?key={apiKey}{query}";

                    var apiResponse = httpKlijent.GetAsync(apiUrl).GetAwaiter().GetResult();
                    result = apiResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (apiResponse.IsSuccessStatusCode)
                    {
                        _cache[requestUrl] = result;
                        Log($"[API SUCCESS] {requestUrl}", ConsoleColor.Green);
                    }
                    else
                    {
                        Log($"[API ERROR] {requestUrl}", ConsoleColor.Red);
                    }
                }
            }

            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(result);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        catch (Exception ex)
        {
            Log($"[SERVER ERROR] {ex.Message}", ConsoleColor.Red);
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes("Došlo je do interne greške na serveru.");
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        finally
        {
            response.OutputStream.Close();
            stopwatch.Stop();
            Log($"Završena obrada {requestUrl} za {stopwatch.ElapsedMilliseconds} ms", ConsoleColor.White);
        }
    }

    public void Stop()
    {
        listenerObj.Stop();
        listenerObj.Close();
        Log("Server zaustavljen.", ConsoleColor.White);
    }

    private static void Log(string message, ConsoleColor color)
    {
        lock (logLock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            Console.ResetColor();
        }
    }
}
