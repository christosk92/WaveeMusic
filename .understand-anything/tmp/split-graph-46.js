const fs = require('fs');
const graph = JSON.parse(fs.readFileSync('C:/WAVEE/WaveeMusic/.understand-anything/tmp/ua-batch46-graph.json', 'utf8'));

const nodes = graph.nodes;
const edges = graph.edges;
const parts = 4;

// Get file nodes sorted alphabetically
const fileNodes = nodes.filter(n => n.type === 'file').map(n => n.filePath).sort();
const chunkSize = Math.ceil(fileNodes.length / parts);

// Split files into part groups
const fileGroups = [];
for (let i = 0; i < parts; i++) {
  fileGroups.push(new Set(fileNodes.slice(i * chunkSize, (i + 1) * chunkSize)));
}

// Assign each node to a part based on its filePath
function getPartForNode(node) {
  const fp = node.filePath || (node.id.startsWith('file:') ? node.id.slice(5) : null);
  if (!fp) return 0;
  for (let i = 0; i < parts; i++) {
    if (fileGroups[i].has(fp)) return i;
  }
  return 0;
}

const partNodes = Array.from({length: parts}, () => []);
nodes.forEach(n => {
  partNodes[getPartForNode(n)].push(n);
});

// Assign edges to parts based on source node
const nodeIdToPart = new Map();
nodes.forEach(n => nodeIdToPart.set(n.id, getPartForNode(n)));

const partEdges = Array.from({length: parts}, () => []);
edges.forEach(e => {
  const p = nodeIdToPart.get(e.source);
  if (p !== undefined) {
    partEdges[p].push(e);
  } else {
    partEdges[0].push(e);
  }
});

// Write each part
for (let i = 0; i < parts; i++) {
  const outPath = 'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-46-part-' + (i + 1) + '.json';
  const fragment = { nodes: partNodes[i], edges: partEdges[i] };
  fs.writeFileSync(outPath, JSON.stringify(fragment));
  console.log('Part ' + (i+1) + ': ' + partNodes[i].length + ' nodes, ' + partEdges[i].length + ' edges -> ' + outPath);
}

// Verify total
const totalN = partNodes.reduce((s, p) => s + p.length, 0);
const totalE = partEdges.reduce((s, p) => s + p.length, 0);
console.log('Total check - nodes:', totalN, 'edges:', totalE);
