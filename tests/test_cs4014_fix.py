"""Source-level regression checks for the intentional fault-observation continuations."""
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[1] / 'src/BannerlordFG.Setup/OptiInstaller.cs').read_text()

def test_generic_timeout_observes_late_error_without_waiting():
    method = SOURCE.split('private static async Task<T> WithDownloadDeadline<T>', 1)[1].split('private static async Task WithDownloadDeadline(', 1)[0]
    assert 'client.CancelAsync();' in method
    assert '_ = operation.ContinueWith(' in method
    assert 'TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously' in method
    assert 'TaskScheduler.Default' in method
    assert 'return await operation;' in method

def test_file_timeout_observes_late_error_without_waiting():
    method = SOURCE.split('private static async Task WithDownloadDeadline(', 1)[1].split('private static string HashFile(', 1)[0]
    assert 'client.CancelAsync();' in method
    assert '_ = operation.ContinueWith(' in method
    assert 'TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously' in method
    assert 'TaskScheduler.Default' in method
    assert 'await operation;' in method
