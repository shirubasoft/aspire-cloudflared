using Aspire.Cloudflared;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose");

builder.AddCloudflared("my-cloudflared");

builder.Build().Run();