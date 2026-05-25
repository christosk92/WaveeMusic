const fs = require('fs');

const data = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-32.json', 'utf8'));

// 71 nodes, 67 edges. parts = ceil(max(71/60, 67/120)) = ceil(max(1.183, 0.558)) = ceil(1.183) = 2
const parts = 2;

// Sort files alphabetically and chunk into 2 groups
const batchFiles = [
  'src/Wavee.UI.WinUI/Controls/RightPanel/FriendsTabView.xaml',
  'src/Wavee.UI.WinUI/Controls/RightPanel/FriendsTabView.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/RightPanel/Lyrics/LyricsCanvasHost.xaml',
  'src/Wavee.UI.WinUI/Controls/RightPanel/Lyrics/LyricsCanvasHost.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/RightPanel/PodcastChapterTimelineRail.xaml',
  'src/Wavee.UI.WinUI/Controls/RightPanel/PodcastChapterTimelineRail.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/RightPanel/QueueTabView.xaml',
  'src/Wavee.UI.WinUI/Controls/RightPanel/QueueTabView.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/RightPanel/RightPanelTabPager.xaml',
  'src/Wavee.UI.WinUI/Controls/RightPanel/RightPanelTabPager.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/RightPanel/RightPanelThemeResolver.cs',
  'src/Wavee.UI.WinUI/Controls/RightPanel/RightPanelView.OutputDevice.cs',
  'src/Wavee.UI.WinUI/Controls/RightPanel/RightPanelView.Properties.cs',
  // Part 2 starts here (ceil(25/2) = 13 per part)
  'src/Wavee.UI.WinUI/Controls/RightPanel/RightPanelView.xaml',
  'src/Wavee.UI.WinUI/Controls/RightPanel/RightPanelView.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/Search/SearchResultHeroCard.AddToPlaylist.cs',
  'src/Wavee.UI.WinUI/Controls/Search/SearchResultHeroCard.xaml',
  'src/Wavee.UI.WinUI/Controls/Search/SearchResultHeroCard.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/Search/SearchResultRowCard.AddToPlaylist.cs',
  'src/Wavee.UI.WinUI/Controls/Search/SearchResultRowCard.xaml',
  'src/Wavee.UI.WinUI/Controls/Search/SearchResultRowCard.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/Search/SearchSubtitleBuilder.cs',
  'src/Wavee.UI.WinUI/Controls/SessionTokens/Helpers/ControlHelpers.cs',
  'src/Wavee.UI.WinUI/Controls/SessionTokens/SessionTokenItem.cs',
  'src/Wavee.UI.WinUI/Controls/SessionTokens/SessionTokenItem.Properties.cs',
];

const part1Files = new Set(batchFiles.slice(0, 13));
const part2Files = new Set(batchFiles.slice(13));

function filePathForNode(node) {
  // For file nodes, use filePath directly
  if (node.filePath) return node.filePath;
  // For function/class nodes, extract from id
  const m = node.id.match(/^(?:function|class):(.+?):[^:]+$/);
  return m ? m[1] : null;
}

const part1Nodes = data.nodes.filter(n => {
  const fp = filePathForNode(n);
  return fp && part1Files.has(fp);
});
const part2Nodes = data.nodes.filter(n => {
  const fp = filePathForNode(n);
  return fp && part2Files.has(fp);
});

const part1NodeIds = new Set(part1Nodes.map(n => n.id));
const part2NodeIds = new Set(part2Nodes.map(n => n.id));

// Edges: source must be in this part's nodes; target can be anywhere
const part1Edges = data.edges.filter(e => part1NodeIds.has(e.source));
const part2Edges = data.edges.filter(e => part2NodeIds.has(e.source));

console.log('Part1: nodes=' + part1Nodes.length + ' edges=' + part1Edges.length);
console.log('Part2: nodes=' + part2Nodes.length + ' edges=' + part2Edges.length);

fs.writeFileSync(
  'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-32-part-1.json',
  JSON.stringify({nodes: part1Nodes, edges: part1Edges}, null, 2),
  {encoding: 'utf8'}
);
fs.writeFileSync(
  'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-32-part-2.json',
  JSON.stringify({nodes: part2Nodes, edges: part2Edges}, null, 2),
  {encoding: 'utf8'}
);
console.log('Written batch-32-part-1.json and batch-32-part-2.json');

// Remove the single-file version
fs.unlinkSync('C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-32.json');
console.log('Removed batch-32.json');
