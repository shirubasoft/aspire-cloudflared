using Aspire.Cloudflared;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose");

// Simple hello world web server using nginx
var helloWorld = builder.AddContainer("hello-world", "nginx", "alpine")
    .WithHttpEndpoint(targetPort: 80, name: "http");

var cloudflareTunnel = builder.AddCloudflareTunnel("my-cloudflare-tunnel");

builder.Build().Run();