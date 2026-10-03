namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>Local lifecycle of the Article Work response publisher.</summary>
internal enum ArticleWorkResponsePublisherState
{
    /// <summary>Constructed and not yet started.</summary>
    Created = 0,

    /// <summary>Opening the confirm-enabled publishing channel.</summary>
    Starting = 1,

    /// <summary>Publications may be admitted.</summary>
    Running = 2,

    /// <summary>New publications blocked; in-flight confirm waits may observe cancellation.</summary>
    Retiring = 3,

    /// <summary>Publish channel disposed; publisher is finished.</summary>
    Stopped = 4,
}
