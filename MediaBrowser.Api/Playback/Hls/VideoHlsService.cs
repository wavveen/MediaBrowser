using MediaBrowser.Common.IO;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaInfo;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using ServiceStack;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MediaBrowser.Api.Playback.Hls
{
    /// <summary>
    /// Class GetHlsVideoStream
    /// </summary>
    [Route("/Videos/{Id}/stream.m3u8", "GET")]
    [Api(Description = "Gets a video stream using HTTP live streaming.")]
    public class GetHlsVideoStream : VideoStreamRequest
    {
        [ApiMember(Name = "BaselineStreamAudioBitRate", Description = "Optional. Specify the audio bitrate for the baseline stream.", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        public int? BaselineStreamAudioBitRate { get; set; }

        [ApiMember(Name = "AppendBaselineStream", Description = "Optional. Whether or not to include a baseline audio-only stream in the master playlist.", IsRequired = false, DataType = "bool", ParameterType = "query", Verb = "GET")]
        public bool AppendBaselineStream { get; set; }

        [ApiMember(Name = "TimeStampOffsetMs", Description = "Optional. Alter the timestamps in the playlist by a given amount, in ms. Default is 1000.", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        public int TimeStampOffsetMs { get; set; }
    }

    /// <summary>
    /// Class GetHlsVideoSegment
    /// </summary>
    [Route("/Videos/{Id}/hls/{PlaylistId}/{SegmentId}.ts", "GET")]
    [Api(Description = "Gets an Http live streaming segment file. Internal use only.")]
    public class GetHlsVideoSegment : VideoStreamRequest
    {
        /// <summary>
        /// Gets or sets the id.
        /// </summary>
        /// <value>The id.</value>
        public string Id { get; set; }

        public string PlaylistId { get; set; }

        /// <summary>
        /// Gets or sets the segment id.
        /// </summary>
        /// <value>The segment id.</value>
        public string SegmentId { get; set; }
    }

    /// <summary>
    /// Class VideoHlsService
    /// </summary>
    public class VideoHlsService : BaseHlsService
    {
        public VideoHlsService(IServerConfigurationManager serverConfig, IUserManager userManager, ILibraryManager libraryManager, IIsoManager isoManager, IMediaEncoder mediaEncoder, IDtoService dtoService, IFileSystem fileSystem, IItemRepository itemRepository, ILiveTvManager liveTvManager)
            : base(serverConfig, userManager, libraryManager, isoManager, mediaEncoder, dtoService, fileSystem, itemRepository, liveTvManager)
        {
        }

        /// <summary>
        /// Gets the specified request.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns>System.Object.</returns>
        public object Get(GetHlsVideoSegment request)
        {
            var file = request.SegmentId + Path.GetExtension(Request.PathInfo);

            file = Path.Combine(ServerConfigurationManager.ApplicationPaths.TranscodingTempPath, file);

            OnBeginRequest(request.PlaylistId);

            return ResultFactory.GetStaticFileResult(Request, file);
        }

        /// <summary>
        /// Called when [begin request].
        /// </summary>
        /// <param name="playlistId">The playlist id.</param>
        protected void OnBeginRequest(string playlistId)
        {
            var normalizedPlaylistId = playlistId.Replace("-low", string.Empty);

            foreach (var playlist in Directory.EnumerateFiles(ServerConfigurationManager.ApplicationPaths.TranscodingTempPath, "*.m3u8")
                .Where(i => i.IndexOf(normalizedPlaylistId, StringComparison.OrdinalIgnoreCase) != -1)
                .ToList())
            {
                ExtendPlaylistTimer(playlist);
            }
        }

        private async void ExtendPlaylistTimer(string playlist)
        {
            ApiEntryPoint.Instance.OnTranscodeBeginRequest(playlist, TranscodingJobType.Hls);

            await Task.Delay(20000).ConfigureAwait(false);

            ApiEntryPoint.Instance.OnTranscodeEndRequest(playlist, TranscodingJobType.Hls);
        }

        /// <summary>
        /// Gets the specified request.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns>System.Object.</returns>
        public object Get(GetHlsVideoStream request)
        {
            return ProcessRequest(request);
        }

        /// <summary>
        /// Gets the audio arguments.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <returns>System.String.</returns>
        protected override string GetAudioArguments(StreamState state)
        {
            var codec = GetAudioCodec(state.Request);

            if (codec.Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                return "-codec:a:0 copy";
            }

            var args = "-codec:a:0 " + codec;

            if (state.AudioStream != null)
            {
                var channels = GetNumAudioChannelsParam(state.Request, state.AudioStream);

                if (channels.HasValue)
                {
                    args += " -ac " + channels.Value;
                }

                var bitrate = GetAudioBitrateParam(state);

                if (bitrate.HasValue)
                {
                    args += " -ab " + bitrate.Value.ToString(UsCulture);
                }

                args += " " + GetAudioFilterParam(state, true);

                return args;
            }

            return args;
        }

        /// <summary>
        /// Gets the video arguments.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="performSubtitleConversion">if set to <c>true</c> [perform subtitle conversion].</param>
        /// <returns>System.String.</returns>
        protected override string GetVideoArguments(StreamState state, bool performSubtitleConversion)
        {
            var codec = GetVideoCodec(state.VideoRequest);

            // See if we can save come cpu cycles by avoiding encoding
            if (codec.Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                return IsH264(state.VideoStream) ? "-codec:v:0 copy -bsf h264_mp4toannexb" : "-codec:v:0 copy";
            }

            const string keyFrameArg = " -force_key_frames expr:if(isnan(prev_forced_t),gte(t,.1),gte(t,prev_forced_t+5))";

            var hasGraphicalSubs = state.SubtitleStream != null && !state.SubtitleStream.IsExternal &&
                                 (state.SubtitleStream.Codec.IndexOf("pgs", StringComparison.OrdinalIgnoreCase) != -1 ||
                                  state.SubtitleStream.Codec.IndexOf("dvd", StringComparison.OrdinalIgnoreCase) != -1);

            var args = "-codec:v:0 " + codec + " " + GetVideoQualityParam(state, "libx264") + keyFrameArg;

            var bitrate = GetVideoBitrateParam(state);

            if (bitrate.HasValue)
            {
                args += string.Format(" -b:v {0} -maxrate ({0}*.80) -bufsize {0}", bitrate.Value.ToString(UsCulture));
            }

            // Add resolution params, if specified
            if (!hasGraphicalSubs)
            {
                if (state.VideoRequest.Width.HasValue || state.VideoRequest.Height.HasValue || state.VideoRequest.MaxHeight.HasValue || state.VideoRequest.MaxWidth.HasValue)
                {
                    args += GetOutputSizeParam(state, codec, performSubtitleConversion);
                }
            }

            var framerate = GetFramerateParam(state);
            if (framerate.HasValue)
            {
                args += string.Format(" -r {0}", framerate.Value.ToString(UsCulture));
            }

            if (!string.IsNullOrEmpty(state.VideoSync))
            {
                args += " -vsync " + state.VideoSync;
            }

            if (!string.IsNullOrEmpty(state.VideoRequest.Profile))
            {
                args += " -profile:v " + state.VideoRequest.Profile;
            }

            if (!string.IsNullOrEmpty(state.VideoRequest.Level))
            {
                args += " -level " + state.VideoRequest.Level;
            }

            // This is for internal graphical subs
            if (hasGraphicalSubs)
            {
                args += GetInternalGraphicalSubtitleParam(state, codec);
            }

            return args;
        }

        /// <summary>
        /// Gets the segment file extension.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <returns>System.String.</returns>
        protected override string GetSegmentFileExtension(StreamState state)
        {
            return ".ts";
        }
    }
}
