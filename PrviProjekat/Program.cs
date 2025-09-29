using System;

//pozivi kojima testiramo funkcionalnost samog Web servera
//http://localhost:8080/?q=Belgrade&days=3&aqi=yes
//http://localhost:8080/?q=Paris&days=1&aqi=no
//http://localhost:8080/?q=NepostojeciGrad

class Program
{
    static void Main(string[] args)
    {
        string url = "http://localhost:8080/";

        WebServerWithThreads server = new WebServerWithThreads(url);

        server.Start();

        Console.WriteLine("Pritisnite Enter za zaustavljanje servera...");
        Console.ReadLine();

        server.Stop();
    }
}