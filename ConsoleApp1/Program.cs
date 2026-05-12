using Google.Protobuf;
using Wavee.Core.Crypto;
using Wavee.Protocol.Playplay;
using Wavee.Protocol.Storage;

var bearer = "";
var tokenHex = "01f62e56cd5435b90dde1a4fdf42af2d";
var tokenAsByte = Convert.FromHexString(tokenHex);
//PlayPlayLicenseRequest(
//     version=5,
//     token=TOKEN,
//     interactivity=Interactivity.INTERACTIVE,
//     content_type=ContentType.AUDIO_TRACK,
//     timestamp=int(time.time()),
// )
var playPlay = new PlayPlayLicenseRequest
{
    Version = 5,
    Token = ByteString.CopyFrom(tokenAsByte),
    Interactivity = Interactivity.Interactive,
    ContentType = ContentType.AudioEpisode,
    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
};

var serialized = playPlay.ToByteArray();
var http = new HttpClient();
//https://gew4-spclient.spotify.com/playplay/v1/key/d2dd5278baf821362403378f086742281012e595
var request = new HttpRequestMessage();
request.Method = HttpMethod.Post;
request.RequestUri = new Uri("https://gew4-spclient.spotify.com/playplay/v1/key/d2dd5278baf821362403378f086742281012e595");
request.Content = new ByteArrayContent(serialized);
//header = application/x-www-form-urlencoded
request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
var x = await http.SendAsync(request);
var responseBytes = await x.Content.ReadAsByteArrayAsync();
var response = PlayPlayLicenseResponse.Parser.ParseFrom(responseBytes);
var kk = "";