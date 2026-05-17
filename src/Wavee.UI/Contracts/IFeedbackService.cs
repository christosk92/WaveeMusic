using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Feedback;

namespace Wavee.UI.Contracts;

public interface IFeedbackService
{
    Task<FeedbackSubmitResponse?> SubmitAsync(FeedbackSubmitRequest request, CancellationToken ct = default);
}