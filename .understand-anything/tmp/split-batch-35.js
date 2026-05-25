const fs = require('fs');

const data = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-35.json', 'utf8'));
const { nodes, edges } = data;

// Collect unique filePaths from file-level nodes, sorted alphabetically
const filePaths = [...new Set(
  nodes.filter(n => n.filePath).map(n => n.filePath)
)].sort();

console.log('Total file paths:', filePaths.length);

const parts = 2;
const chunkSize = Math.ceil(filePaths.length / parts);

// Split file paths into chunks
const chunks = [];
for (let i = 0; i < parts; i++) {
  chunks.push(filePaths.slice(i * chunkSize, (i + 1) * chunkSize));
}
chunks.forEach((c, i) => console.log('Part', i+1, 'files:', c.length, c[0], '...', c[c.length-1]));

// For each part, collect nodes whose filePath is in that chunk
for (let k = 0; k < parts; k++) {
  const chunkPaths = new Set(chunks[k]);
  const partNodes = nodes.filter(n => chunkPaths.has(n.filePath));
  const partNodeIds = new Set(partNodes.map(n => n.id));
  // Edges whose source is in this part's nodes
  const partEdges = edges.filter(e => partNodeIds.has(e.source));

  const part = { nodes: partNodes, edges: partEdges };
  const outPath = `C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-35-part-${k+1}.json`;
  fs.writeFileSync(outPath, JSON.stringify(part, null, 2));
  console.log(`Part ${k+1}: ${partNodes.length} nodes, ${partEdges.length} edges -> ${outPath}`);
}

// Remove the single-part file since we have split parts
fs.unlinkSync('C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-35.json');
console.log('Removed batch-35.json (split into parts)');
