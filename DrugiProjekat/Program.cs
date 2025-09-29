using System;

//pozivi kojima testiramo funkcionalnost samog Web servera
//http://localhost:8080/?q=Belgrade&days=3&aqi=yes
//http://localhost:8080/?q=Paris&days=1&aqi=no
//http://localhost:8080/?q=NepostojeciGrad

class Program
{
    private static void Main()
    {
        var server = new WebServerWithThreadPool("http://localhost:8080/");
        server.Start();

        Console.WriteLine("Server radi u pozadini. Pritisni Enter za izlaz...");
        Console.ReadLine();

        server.Stop();
    }
}