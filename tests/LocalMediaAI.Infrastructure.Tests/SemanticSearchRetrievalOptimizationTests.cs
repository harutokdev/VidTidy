using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class SemanticSearchRetrievalOptimizationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAISemanticRetrievalTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root);
        _log = new JsonFileLogger(_paths);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _log.Dispose();
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; Directory.Exists(_root) && attempt < 8; attempt++)
        {
            try { Directory.Delete(_root, true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }

    [Fact]
    public async Task CurrentSemanticSearchRowsReturnsOnlyCurrentPresentRowsInStableOrder()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var firstRootPath = Path.Combine(_root, "semantic-current-a");
        var secondRootPath = Path.Combine(_root, "semantic-current-b");
        Directory.CreateDirectory(firstRootPath);
        Directory.CreateDirectory(secondRootPath);
        var firstRoot = await repository.AddOrGetLibraryRootAsync(firstRootPath, CancellationToken.None);
        var secondRoot = await repository.AddOrGetLibraryRootAsync(secondRootPath, CancellationToken.None);
        var model = SemanticModelRegistry.ResolveDevelopmentCandidate();
        var execution = SemanticExecutionProfile.Resolve(model);

        await InsertSemanticRowAsync(firstRoot.LibraryRootId, "beta.mp4", model, execution, model.ModelId, "Completed", "Completed", "Present");
        await InsertSemanticRowAsync(firstRoot.LibraryRootId, "alpha.mp4", model, execution, model.ModelId, "Completed", "Completed", "Present");
        await InsertSemanticRowAsync(firstRoot.LibraryRootId, "stale-model.mp4", model, execution, "other/model", "Completed", "Completed", "Present");
        await InsertSemanticRowAsync(firstRoot.LibraryRootId, "failed-sample.mp4", model, execution, model.ModelId, "Completed", "Failed", "Present");
        await InsertSemanticRowAsync(firstRoot.LibraryRootId, "missing.mp4", model, execution, model.ModelId, "Completed", "Completed", "Missing");
        await InsertSemanticRowAsync(secondRoot.LibraryRootId, "other-root.mp4", model, execution, model.ModelId, "Completed", "Completed", "Present");

        var rows = await repository.GetCurrentSemanticSearchRowsAsync(model, execution, firstRoot.LibraryRootId, CancellationToken.None);

        Assert.Equal(new[] { "alpha.mp4", "beta.mp4" }, rows.Select(row => row.FileName));
        Assert.All(rows, row =>
        {
            Assert.Equal(firstRoot.LibraryRootId, row.LibraryRootId);
            Assert.Equal(model.Dimension * sizeof(float), row.VectorFloat32.Length);
            Assert.Equal(0d, row.PresentationTimestampSeconds);
            Assert.Null(row.FrameRelativePath);
            Assert.Null(row.DurationSeconds);
        });
    }

    private async Task InsertSemanticRowAsync(Guid rootId, string fileName, SemanticModelProfile model, SemanticExecutionProfile execution, string persistedModelName, string embeddingStatus, string sampleStatus, string presenceState)
    {
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var contentId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var sampleId = Guid.NewGuid();
        var embeddingId = Guid.NewGuid();
        var vector = Enumerable.Repeat((float)(1d / Math.Sqrt(model.Dimension)), model.Dimension).ToArray();
        var vectorBytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, vectorBytes, 0, vectorBytes.Length);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO media_content(media_content_id,canonical_sha256,byte_length,identity_state,created_at) VALUES($content,NULL,1,'Unverified',$now); INSERT INTO media_file(media_file_id,library_root_id,media_content_id,path,normalized_path,file_name,extension,size_bytes,creation_time_utc,last_write_time_utc,volume_identity,file_identity,quick_fingerprint,full_sha256,presence_state,first_seen_at,last_seen_at,last_scan_id) VALUES($file,$root,$content,$path,$normalized,$name,'.mp4',1,$now,$now,NULL,NULL,$identity,NULL,$presence,$now,$now,NULL); INSERT INTO visual_sample(visual_sample_id,media_file_id,media_content_id,input_identity_hash,sample_index,requested_timestamp_seconds,presentation_timestamp_seconds,sampling_version,cache_key,frame_relative_path,frame_sha256,frame_byte_size,status,error_code,error_message,created_at,updated_at) VALUES($sample,$file,$content,$identity,0,0,0,$sampling,$sampleCache,NULL,NULL,NULL,$sampleStatus,NULL,NULL,$now,$now); INSERT INTO visual_embedding(visual_embedding_id,visual_sample_id,input_identity_hash,cache_key,model_name,model_revision,processor_version,sampling_version,dtype,dimension,normalized,vector_float32,status,error_code,error_message,created_at,updated_at) VALUES($embedding,$sample,$identity,$embeddingCache,$model,$revision,$processor,$sampling,$dtype,$dimension,1,$vector,$embeddingStatus,NULL,NULL,$now,$now);";
        command.Parameters.AddWithValue("$content", contentId.ToString("D"));
        command.Parameters.AddWithValue("$file", fileId.ToString("D"));
        command.Parameters.AddWithValue("$root", rootId.ToString("D"));
        var path = Path.Combine(_root, fileName);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$normalized", PathPolicy.NormalizeForIdentity(path));
        command.Parameters.AddWithValue("$name", fileName);
        command.Parameters.AddWithValue("$identity", "semantic-row-" + fileId.ToString("N"));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$presence", presenceState);
        command.Parameters.AddWithValue("$sample", sampleId.ToString("D"));
        command.Parameters.AddWithValue("$sampling", VisualSamplingPolicy.FixedIntervalV1.Version);
        command.Parameters.AddWithValue("$sampleCache", "sample-" + sampleId.ToString("N"));
        command.Parameters.AddWithValue("$sampleStatus", sampleStatus);
        command.Parameters.AddWithValue("$embedding", embeddingId.ToString("D"));
        command.Parameters.AddWithValue("$embeddingCache", "embedding-" + embeddingId.ToString("N"));
        command.Parameters.AddWithValue("$model", persistedModelName);
        command.Parameters.AddWithValue("$revision", model.ModelRevision);
        command.Parameters.AddWithValue("$processor", model.ProcessorVersion);
        command.Parameters.AddWithValue("$dtype", execution.Dtype);
        command.Parameters.AddWithValue("$dimension", model.Dimension);
        command.Parameters.AddWithValue("$vector", vectorBytes);
        command.Parameters.AddWithValue("$embeddingStatus", embeddingStatus);
        await command.ExecuteNonQueryAsync();
    }
}
