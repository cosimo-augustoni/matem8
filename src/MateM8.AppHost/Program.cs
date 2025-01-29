var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.MateM8_ApiService>("apiservice");

builder.Build().Run();
