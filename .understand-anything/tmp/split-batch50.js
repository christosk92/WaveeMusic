const fs = require('fs');
const full = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-batch50-full.json', 'utf8'));

const { nodes, edges } = full;

// Get all file-level nodes (type: file) sorted by filePath alphabetically
const fileNodes = nodes.filter(n => n.type === 'file').sort((a, b) => a.filePath.localeCompare(b.filePath));
const totalFiles = fileNodes.length; // 25

const parts = 3;
const chunkSize = Math.ceil(totalFiles / parts);

// Partition files into chunks
const fileChunks = [];
for (let i = 0; i < parts; i++) {
  fileChunks.push(fileNodes.slice(i * chunkSize, (i + 1) * chunkSize).map(n => n.filePath));
}

console.log('File chunks:');
fileChunks.forEach((chunk, i) => console.log('Part', i+1, ':', chunk.length, 'files'));

// For each part: all nodes whose filePath is in this part's files
// For non-file nodes (class/function), use their filePath field
for (let k = 0; k < parts; k++) {
  const partFiles = new Set(fileChunks[k]);

  const partNodes = nodes.filter(n => {
    if (n.filePath) return partFiles.has(n.filePath);
    return false;
  });

  // All edges whose source node is in this part
  const partNodeIds = new Set(partNodes.map(n => n.id));
  const partEdges = edges.filter(e => partNodeIds.has(e.source));

  const output = { nodes: partNodes, edges: partEdges };
  const outPath = 'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-50-part-' + (k+1) + '.json';
  fs.writeFileSync(outPath, JSON.stringify(output), 'utf8');
  console.log('Part', k+1, ': wrote', partNodes.length, 'nodes,', partEdges.length, 'edges to', outPath);
}
