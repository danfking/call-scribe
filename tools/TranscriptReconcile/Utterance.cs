namespace TranscriptReconcile;

/// <summary>One line of a transcript on a common axis. <see cref="StartSec"/> is seconds from
/// that stream's own start (the offset estimator later corrects for streams that began at
/// slightly different moments). Speaker is the raw label from that source: a Teams display
/// name, a call-scribe live label ("Speaker 7"/"Dan"), or a final-pass diarized name.</summary>
public sealed record Utterance(double StartSec, double? EndSec, string Speaker, string Text);

/// <summary>Which transcript a set of utterances came from.</summary>
public enum Source { Teams, Live, Final }
