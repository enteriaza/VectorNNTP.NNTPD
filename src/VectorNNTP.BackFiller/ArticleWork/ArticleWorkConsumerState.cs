namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>Local lifecycle of one backbone consumer session.</summary>
public enum ArticleWorkConsumerState
{
    /// <summary>Constructed and not yet started.</summary>
    Created = 0,

    /// <summary>Opening the consumer channel.</summary>
    Starting = 1,

    /// <summary>Deliveries may be admitted.</summary>
    Running = 2,

    /// <summary>New admissions blocked; admitted work is draining.</summary>
    Retiring = 3,

    /// <summary>Channel disposed; session is finished.</summary>
    Stopped = 4,
}
