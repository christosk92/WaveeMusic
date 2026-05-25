const fs = require('fs');
const graph = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-graph-51.json', 'utf8'));
const data = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-file-extract-results-51.json', 'utf8'));

const outDir = 'C:/WAVEE/WaveeMusic/.understand-anything/intermediate';

// Get sorted file paths
const filePaths = data.results.map(function(r) { return r.path; }).sort();
const numParts = 7;
const filesPerPart = Math.ceil(filePaths.length / numParts);

console.log('Files:', filePaths.length, 'Parts:', numParts, 'Files per part:', filesPerPart);

for (let p = 0; p < numParts; p++) {
  const partFiles = filePaths.slice(p * filesPerPart, (p + 1) * filesPerPart);
  const partFileSet = new Set(partFiles);

  // All nodes whose filePath is in this part's files
  const partNodes = graph.nodes.filter(function(n) {
    return n.filePath && partFileSet.has(n.filePath);
  });

  const partNodeIds = new Set(partNodes.map(function(n) { return n.id; }));

  // All edges whose source is in this part's nodes
  const partEdges = graph.edges.filter(function(e) {
    return partNodeIds.has(e.source);
  });

  const outPath = outDir + '/batch-51-part-' + (p + 1) + '.json';
  fs.writeFileSync(outPath, JSON.stringify({ nodes: partNodes, edges: partEdges }));
  console.log('Part', p + 1, ':', partFiles.length, 'files,', partNodes.length, 'nodes,', partEdges.length, 'edges ->', outPath);
}
console.log('Done');
