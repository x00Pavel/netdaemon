// See https://aka.ms/new-console-template for more information

using NetDaemon.HassClient.Tests.Integration;

Console.WriteLine("Starting TestServer");
var haMock = new HomeAssistantMock();
haMock.HomeAssistantHost.WaitForShutdown();