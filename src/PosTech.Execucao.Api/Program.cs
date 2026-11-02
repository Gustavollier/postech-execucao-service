using System.Text.Json.Serialization;
using MongoDB.Driver;
using PosTech.Contratos;
using PosTech.Execucao;
using PosTech.Execucao.Api.Aplicacao;
using PosTech.Execucao.Api.Controllers;
using PosTech.Mensageria;

var builder = WebApplication.CreateBuilder(args);

// As duas conexões são obrigatórias e o serviço se recusa a subir sem elas:
// falhar na partida é melhor que descobrir na primeira mensagem da fila.
var mongoConnectionString = builder.Configuration["MongoConnectionString"]
    ?? throw new InvalidOperationException("MongoConnectionString não configurada.");

var rabbitConnectionString = builder.Configuration["RabbitMqConnectionString"]
    ?? throw new InvalidOperationException("RabbitMqConnectionString não configurada.");

var nomeDoBanco = builder.Configuration["MongoDatabase"] ?? "execucao-db";

builder.Services.AddControllers(opcoes => opcoes.Filters.Add<TradutorDeErros>())
    .AddJsonOptions(opcoes => opcoes.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

builder.Logging.AddJsonConsole(opcoes =>
{
    opcoes.IncludeScopes = true;
    opcoes.UseUtcTimestamp = true;
});

builder.Services.AddSingleton<IMongoDatabase>(_ =>
    new MongoClient(mongoConnectionString).GetDatabase(nomeDoBanco));

builder.Services.AddScoped<IOrdemRepositorio>(sp => new OrdemRepositorio(sp.GetRequiredService<IMongoDatabase>()));
builder.Services.AddScoped<IPecaRepositorio>(sp => new PecaRepositorio(sp.GetRequiredService<IMongoDatabase>()));
builder.Services.AddScoped(sp => new ServicoDaOficina(
    sp.GetRequiredService<IOrdemRepositorio>(),
    sp.GetRequiredService<IPecaRepositorio>()));
builder.Services.AddScoped<ManipuladorDeEventos>();

builder.Services.AddSingleton<IRepositorioDeOutbox>(sp =>
    new OutboxMongo(sp.GetRequiredService<IMongoDatabase>()));

// Idempotência do consumidor: o RabbitMQ entrega ao menos uma vez.
builder.Services.AddSingleton<IRegistroDeIdempotencia>(sp =>
    new IdempotenciaMongo(sp.GetRequiredService<IMongoDatabase>()));

builder.Services.AddSingleton(_ => new ConexaoRabbitMq(rabbitConnectionString, GestorDaExecucao.Origem));
builder.Services.AddSingleton<IPublicadorDeEventos>(sp =>
    new PublicadorRabbitMq(sp.GetRequiredService<ConexaoRabbitMq>()));
builder.Services.AddSingleton(sp => new ProcessadorDoOutbox(
    sp.GetRequiredService<IRepositorioDeOutbox>(),
    sp.GetRequiredService<IPublicadorDeEventos>()));

builder.Services.AddHostedService<PublicadorDoOutbox>();
builder.Services.AddHostedService<ConsumidorDaExecucao>();
builder.Services.AddHostedService<CriadorDeIndices>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/health/ready", () => Results.Ok(new { status = "pronto" }));

app.Run();

/// <summary>Exposto para os testes de integração subirem a API em memória.</summary>
public partial class Program;
