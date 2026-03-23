using Microsoft.AspNetCore.Mvc;
using VoxMind.Api.DTOs;
using VoxMind.Core.SpeakerRecognition;

namespace VoxMind.Api.Endpoints;

public static class SpeakerEndpoints
{
    public static IEndpointRouteBuilder MapSpeakerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/speakers")
            .WithTags("Speakers");

        group.MapGet("/", GetAllSpeakersAsync)
            .Produces<IReadOnlyList<SpeakerResponse>>()
            .WithSummary("Lister tous les locuteurs");

        group.MapPost("/", CreateSpeakerAsync)
            .Produces<SpeakerResponse>(201)
            .ProducesProblem(400)
            .WithSummary("Créer un nouveau locuteur");

        group.MapPatch("/{id:guid}", RenameSpeakerAsync)
            .Produces<SpeakerResponse>()
            .ProducesProblem(404)
            .WithSummary("Renommer un locuteur");

        group.MapDelete("/{id:guid}", DeleteSpeakerAsync)
            .Produces(204)
            .ProducesProblem(404)
            .WithSummary("Supprimer un locuteur et ses empreintes");

        group.MapPost("/{id:guid}/embeddings", AddEmbeddingAsync)
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data")
            .Produces(204)
            .ProducesProblem(400)
            .ProducesProblem(404)
            .WithSummary("Ajouter une empreinte vocale depuis un fichier audio");

        group.MapPost("/unknown", ReportUnknownAsync)
            .Produces(204)
            .ProducesProblem(400)
            .WithSummary("Signaler et corriger un locuteur mal identifié");

        return app;
    }

    private static async Task<IResult> GetAllSpeakersAsync(
        ISpeakerIdentificationService svc, CancellationToken ct)
    {
        var profiles = await svc.GetAllProfilesAsync();
        var response = profiles.Select(ToResponse).ToList();
        return Results.Ok(response);
    }

    private static async Task<IResult> CreateSpeakerAsync(
        [FromBody] CreateSpeakerRequest request,
        ISpeakerIdentificationService svc,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Problem("Le nom du locuteur est requis.", statusCode: 400);

        var profile = await svc.EnrollSpeakerAsync(request.Name, Array.Empty<float>(), 0f);
        return Results.Created($"/v1/speakers/{profile.Id}", ToResponse(profile));
    }

    private static async Task<IResult> RenameSpeakerAsync(
        Guid id,
        [FromBody] RenameSpeakerRequest request,
        ISpeakerIdentificationService svc,
        CancellationToken ct)
    {
        var profile = await svc.GetProfileAsync(id);
        if (profile is null)
            return Results.Problem($"Locuteur '{id}' introuvable.", statusCode: 404);

        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Problem("Le nom est requis.", statusCode: 400);

        await svc.RenameProfileAsync(id, request.Name);
        var updated = await svc.GetProfileAsync(id);
        return Results.Ok(ToResponse(updated!));
    }

    private static async Task<IResult> DeleteSpeakerAsync(
        Guid id,
        ISpeakerIdentificationService svc,
        CancellationToken ct)
    {
        var profile = await svc.GetProfileAsync(id);
        if (profile is null)
            return Results.Problem($"Locuteur '{id}' introuvable.", statusCode: 404);

        await svc.DeleteProfileAsync(id);
        return Results.NoContent();
    }

    private static async Task<IResult> AddEmbeddingAsync(
        Guid id,
        IFormFile file,
        ISpeakerIdentificationService svc,
        ILogger<ISpeakerIdentificationService> logger,
        CancellationToken ct)
    {
        var profile = await svc.GetProfileAsync(id);
        if (profile is null)
            return Results.Problem($"Locuteur '{id}' introuvable.", statusCode: 404);

        if (file.Length == 0)
            return Results.Problem("Le fichier audio est vide.", statusCode: 400);

        byte[] audioBytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            audioBytes = ms.ToArray();
        }

        var embedding = await svc.ExtractEmbeddingAsync(audioBytes, ct);
        if (embedding is null)
            return Results.Problem("Impossible d'extraire l'empreinte depuis ce fichier audio.", statusCode: 422);

        await svc.AddEmbeddingToProfileAsync(id, embedding, confidence: 1.0f);
        return Results.NoContent();
    }

    private static async Task<IResult> ReportUnknownAsync(
        [FromBody] ReportUnknownRequest request,
        ISpeakerIdentificationService svc,
        CancellationToken ct)
    {
        if (request.CorrectSpeakerId.HasValue)
        {
            await svc.LinkSpeakersAsync(request.CorrectSpeakerId.Value, request.UnknownSpeakerId);
        }
        else if (!string.IsNullOrWhiteSpace(request.CorrectName))
        {
            await svc.RenameProfileAsync(request.UnknownSpeakerId, request.CorrectName);
        }
        else
        {
            return Results.Problem(
                "Fournir correct_speaker_id ou correct_name.", statusCode: 400);
        }

        return Results.NoContent();
    }

    private static SpeakerResponse ToResponse(VoxMind.Core.SpeakerRecognition.SpeakerProfile p) =>
        new(p.Id, p.Name, p.CreatedAt, p.Embeddings.Count, p.LastSeenAt, p.DetectionCount);
}
