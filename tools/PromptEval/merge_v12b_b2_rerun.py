import json

annotations = [
  # chunk01
  {"id": "FE1601CC-43FD-4156-B1C2-CC6136D23B4A", "gaps": ["gender_violation", "color_specific"]},      # man/woman; dark green polo, brown hair, light-colored sweater
  {"id": "83F13182-638F-4EF2-86E6-A6097E1470A1", "gaps": []},                                          # no gender/color issues; trailing bullet artifact harmless
  {"id": "E28DA151-11CB-4878-AA46-233CA0F2B07A", "gaps": ["gender_violation", "color_specific"]},      # girl/She; white skirt, blonde hair, purple sandals
  {"id": "F3E3578F-23BA-4FC0-A4B3-CF07B4D10EAE", "gaps": ["gender_violation", "color_specific"]},      # girl x3; black coat, blue pants, blue coat, pink gloves, pink coat, purple pants
  {"id": "803DA822-9156-4C78-8024-9CF8A8649269", "gaps": ["color_specific"]},                          # blue rug, white/pink bunny ears
  {"id": "0C8F5DB8-118C-4802-BC94-5D3309947CE2", "gaps": ["color_specific"]},                          # red tree skirt
  {"id": "7B04B1D1-5D28-462B-B016-70A791612B83", "gaps": ["gender_violation", "color_specific"]},      # girl/She/Her; blonde hair, orange t-shirt, brown skirt, blue chair
  {"id": "717C1357-8261-4BE7-BA2D-F818D68711FC", "gaps": ["gender_violation", "color_specific"]},      # woman/boy/She; red sweater, brown hair, red shirt
  {"id": "50DF9D65-96F7-405C-B2F6-8D60D79D51FB", "gaps": ["gender_violation", "color_specific"]},      # man/girl/his/woman/man; white shirt, black pants, black/white dress, green/white shirt
  {"id": "51A7C912-4708-4A9C-9555-F341A3591EC7", "gaps": ["color_specific"]},                          # brown hair, red collar (ambiguous whether person or dog wears it)
  # chunk02
  {"id": "7B5A93E0-2D66-413C-B3BF-8FF6E301F054", "gaps": ["gender_violation", "color_specific"]},      # woman/her/She/Her; light-brown hair, gold medal, white cap, red/blue/yellow ribbon
  {"id": "268A261F-4861-49BB-89CD-30483A58D874", "gaps": ["gender_violation", "color_specific"]},      # girl/her x3; green shirt, silver star, pink/white paper
  {"id": "240E6E60-9F2A-45C7-9D27-2CE217516A2D", "gaps": []},                                          # repetition loop — rejected by parser, not scored
  {"id": "6C9B339B-6400-4910-B6CF-0C39096939C8", "gaps": ["gender_violation", "color_specific"]},      # man x2/boy; gold letters, white/blue striped shirt, gold sword
  {"id": "AD08C021-25E8-4935-8748-ADBA4BAF1B87", "gaps": ["gender_violation", "color_specific"]},      # man/He/him; dark suit, white shirt
  {"id": "5CAF2315-786C-4640-9ED0-6E4B22383ACB", "gaps": ["color_specific"]},                          # white shirt, black pants (driver's clothing)
  {"id": "4C49294A-1481-451F-BA4F-A9412569AD77", "gaps": ["gender_violation", "wrong_count", "color_specific"]},  # pirate's wife (gendered); lists 4 child costumes but says 3; purple cat
  {"id": "DB8B0196-5A1C-4FD3-8990-83DCB8F23A49", "gaps": []},                                          # rerun uses children/adult throughout — no gender_violation
  {"id": "11D84F96-42F2-461E-986B-E18AD5E6C7C5", "gaps": ["gender_violation", "color_specific"]},      # man x2/woman x2; blue/red/white/black t-shirts, red cover
  {"id": "3590CB91-B148-4222-8B5B-CD7B78CFE494", "gaps": ["gender_violation", "color_specific"]},      # woman/She; red shirt (dark hair is general, not flagged)
  # chunk03
  {"id": "5DD8CC35-0C0B-471B-B54C-938905DB2451", "gaps": []},                                          # rerun uses people/children/adults — no violations
  {"id": "2EA312CA-0D03-4A03-93B6-687C35A375F7", "gaps": ["color_specific", "wrong_count"]},           # purple/black uniforms, light purple shirt, black collar; wrong count carried from original
  {"id": "6E522929-89F4-49D4-81B4-E098ECB3BCFF", "gaps": ["gender_violation", "color_specific"]},      # girl/her x3/She/Her; purple cape, black turtleneck, blue eyes
  {"id": "A57128EB-5EAD-4998-86C5-5517C5AA518F", "gaps": ["gender_violation", "color_specific"]},      # girl/She x2; red shirt, blue jeans, purple flower, blonde hair, white flowers
  {"id": "59E9DF35-0ABC-45DF-A8FE-DC7C06F869AD", "gaps": ["gender_violation", "color_specific"]},      # girl/her x2/She; blonde hair, red plaid shawl, light blue skirt, white tights, black shoes
  {"id": "8CE7C769-F554-4DE4-812A-DE5BB1F1C2DE", "gaps": ["gender_violation", "color_specific", "hallucination"]},  # boy/He/his; red Puma shoe box (reads brand label — hallucination)
  {"id": "08F18761-8F82-4F9D-99D2-6ED97AEF4DCD", "gaps": ["color_specific"]},                          # purple shirt (adult's clothing)
  {"id": "EE4FF6B6-8644-4F0B-A3BE-AE0619D3FA5B", "gaps": []},                                          # firework scene — no violations
  {"id": "6BECAE51-9975-4C8D-BC5A-12FD937AEFB4", "gaps": ["gender_violation", "color_specific"]},      # man x2/woman x2; white hair, black/grey sweater, blonde hair, red cardigan
  {"id": "D7892995-7AAA-466D-80E9-30414C077E7E", "gaps": ["gender_violation", "color_specific"]},      # girl/woman/She; yellow/orange/green shirt, yellow cake with pink/blue/purple dots
  # chunk04
  {"id": "3F27EDC0-9A8B-4482-ACD7-8D305E7E2059", "gaps": ["gender_violation", "color_specific"]},      # girl x2/her/She; orange/pink tutu, orange hair tie, pink/yellow tutu
  {"id": "9EBA4AF8-B9CE-45F4-86CE-F0A26AADE266", "gaps": ["gender_violation", "color_specific", "truncated"]},  # man x3/woman; short brown hair; very brief/abrupt ending
  {"id": "635CD5A5-81B5-41F1-92C0-A7B0072BF590", "gaps": []},                                          # prairie dogs — no violations
  {"id": "B6F41B2F-92F9-4F56-ABB4-B8D3F758276F", "gaps": ["hallucination", "wrong_count", "color_specific"]},  # "15 people" hallucinated for a cupcake tray; green frosting, blue/gold tablecloth
  {"id": "212ECA54-B994-4CA8-B674-718AC8C7E8BB", "gaps": []},                                          # underwater pool shot — no violations
  {"id": "3016206A-1CEB-44CC-95F8-C5DCDF4F5F13", "gaps": ["gender_violation", "color_specific"]},      # girl x2/her x3/She; purple swimsuit, white card, pink/blue design, green wall
  {"id": "B2D28827-1B8F-4086-AC87-DCAFB3331EE8", "gaps": ["color_specific"]},                          # green water slide
  {"id": "931EAC11-D22F-4E1B-8BB0-172B19D63411", "gaps": ["color_specific"]},                          # rerun uses children/adult — no gender_violation; black/white checkered dress, blue dress
  {"id": "09E138A1-05B0-4675-BA07-EFD47D59B39C", "gaps": ["color_specific"]},                          # red and black uniforms, red t-shirts
  {"id": "F80B616F-2F53-4C90-9DF8-9DFDB45DC72D", "gaps": ["color_specific"]},                          # black/white helmet, black/white jacket (clothing)
  # chunk05
  {"id": "B4EA9AD7-C05E-4F1A-9F40-1560D836CEAA", "gaps": ["gender_violation", "color_specific"]},      # man x2/woman; orange shirt, black pants, blue/white striped shirt, khaki pants
  {"id": "73861F60-3D15-4462-84B9-A7A01DF21BC7", "gaps": ["gender_violation", "color_specific"]},      # woman x2; maroon shirt (landmark text IS quoted in rerun)
  {"id": "59644B20-C946-47EF-B83B-2097EF4B3829", "gaps": ["gender_violation"]},                        # woman x2/girl x2/her; no specific clothing colors mentioned
  {"id": "133B2439-1910-4173-B3E0-1C370B9E3986", "gaps": ["color_specific"]},                          # red and white tracksuits (clothing); no count stated so wrong_count n/a
  {"id": "4D662B61-7842-49D1-ACF0-629239DEA859", "gaps": ["color_specific", "hallucination"]},         # green/black suit; contradicts self on cap color (black vs white) = hallucination
  {"id": "D1622623-D810-418D-AC6B-E4502A656211", "gaps": ["gender_violation", "color_specific"]},      # man x3/girl; blue t-shirt, white t-shirt, blue shorts
  {"id": "F3C78BE5-5447-453F-8225-10AEA7714DB4", "gaps": []},                                          # rerun uses children/adults — no violations
  {"id": "0BAB1014-0744-4E6A-913A-7C1DD1BEB385", "gaps": ["color_specific"]},                          # light blue shirt (clothing), red/green/yellow mat
  {"id": "0360C315-7D53-4051-AFF5-1E8CDD44FEB3", "gaps": []},                                          # bedroom scene — no person, no violations
  {"id": "EE4EF733-5AD1-498A-92D8-C8A9725D6245", "gaps": []},                                          # group scene — no gendered terms, no specific clothing colors
]

gap_map = {a["id"].upper(): a["gaps"] for a in annotations}
print(f"Annotations: {len(gap_map)}")

batch_path = "C:/dev/PhotoIQPro/tools/PromptEval/batches/batch_20260411_171400_rerun_20260411_192615.json"
with open(batch_path) as f:
    batch = json.load(f)

photos = batch["photos"]
print(f"Photos in batch: {len(photos)}")

annotated = []
missing = []
for p in photos:
    pid = p["id"].upper()
    if pid in gap_map:
        p = dict(p)
        p["gaps"] = gap_map[pid]
        p["oracle_desc"] = None
    else:
        missing.append(pid)
    annotated.append(p)

if missing:
    print(f"MISSING: {missing}")
else:
    print("All annotated.")

batch["photos"] = annotated
batch["annotated_at"] = "2026-04-11T00:00:00Z"

out = batch_path.replace(".json", "_annotated.json")
with open(out, "w") as f:
    json.dump(batch, f, indent=2)
print(f"Saved: {out}")
