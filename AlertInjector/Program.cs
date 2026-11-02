using AlertInjector;

// Dialog nho phat canh bao gia cho NodePublisherSim. Cong dieu khien mac dinh http://127.0.0.1:5199 (SIM_CONTROL_PORT).
ApplicationConfiguration.Initialize();
var port = Environment.GetEnvironmentVariable("SIM_CONTROL_PORT") is { Length: > 0 } p ? p : "5199";
Application.Run(new MainForm(new SimClient(new Uri($"http://127.0.0.1:{port}/"))));
