using UnifiedPOCAPI.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.Configure<DatabricksOptions>(builder.Configuration.GetSection("Databricks"));
builder.Services.Configure<MicrosoftGraphOptions>(builder.Configuration.GetSection("MicrosoftGraph"));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddCors();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapControllers();

app.Run();

