using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace PetDaDog.Unity
{
    internal static class UnityWebRequestTaskExtensions
    {
        public static Task SendAsync(this UnityWebRequest request, CancellationToken cancellationToken)
        {
            var operation = request.SendWebRequest();
            var completion = new TaskCompletionSource<bool>();
            operation.completed += _ => completion.TrySetResult(true);
            if (operation.isDone)
            {
                completion.TrySetResult(true);
            }

            if (!cancellationToken.CanBeCanceled)
            {
                return completion.Task;
            }

            return AwaitWithCancellationAsync(completion.Task, request, cancellationToken);
        }

        private static async Task AwaitWithCancellationAsync(Task completion, UnityWebRequest request, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(request.Abort);
            await completion;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
