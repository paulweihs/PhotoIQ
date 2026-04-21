SELECT 
  er.file_name,
  er.similarity,
  rc.claude_description,
  er.ollama_description
FROM evaluation_results er
JOIN reference_corpus rc ON er.file_name = rc.file_name
WHERE er.run_id = '2026-04-20T20-38-31'
ORDER BY er.similarity ASC
LIMIT 3;
