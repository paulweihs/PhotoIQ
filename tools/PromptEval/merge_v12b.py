import json

annotations = [
  # chunk01
  {"id": "9A9BCE70-BBDF-4A86-990C-F93D7FEEA5A6", "gaps": ["gender_violation", "color_specific"]},
  {"id": "E2134910-551D-4531-B05D-3603E4A6C862", "gaps": ["gender_violation", "color_specific"]},
  {"id": "946F3898-2E19-41B9-8B0F-8D5823FA481E", "gaps": ["gender_violation", "color_specific"]},
  {"id": "3DB61929-FD05-4A3D-8E53-41D3C6F65C71", "gaps": ["gender_violation", "color_specific"]},
  {"id": "6FBC474C-87A7-4065-8087-61CF30B84355", "gaps": []},
  {"id": "EDCFFEA2-C67E-4633-877E-89383F4E988A", "gaps": ["hallucination"]},
  {"id": "33CF4858-84C6-4764-9988-73D7CB225F8F", "gaps": ["gender_violation", "color_specific"]},
  {"id": "5943D273-E45C-4C3B-A592-1669F6E30915", "gaps": ["gender_violation", "wrong_count"]},
  {"id": "F001D297-3D24-4475-84E5-1AFA74D46094", "gaps": ["gender_violation", "wrong_count"]},
  {"id": "C8707803-E83D-40F6-8270-18E938848DC2", "gaps": ["gender_violation", "color_specific"]},
  # chunk02
  {"id": "ABD5636A-0020-45E1-934D-D9FCDECEB37F", "gaps": ["gender_violation", "color_specific", "wrong_count"]},
  {"id": "9359B0EB-C288-479A-889E-B477D3E4E69E", "gaps": ["gender_violation", "color_specific", "activity_inversion"]},
  {"id": "4C0981E9-95BB-461A-A150-D4C5EB1958FE", "gaps": ["gender_violation", "color_specific"]},
  {"id": "9D48D353-8ABD-4073-A075-BAA60CC5847C", "gaps": ["color_specific"]},
  {"id": "9361D533-D201-4893-9AA1-0E2FF3156762", "gaps": ["gender_violation", "color_specific"]},
  {"id": "610CBB9E-87A9-4A25-9568-508181EDA5E4", "gaps": ["hallucination"]},
  {"id": "F1379217-DE5E-4D25-8430-60D0687CEE10", "gaps": ["color_specific"]},
  {"id": "709A1719-57D8-4EC8-B577-1ABA784F531D", "gaps": ["gender_violation"]},
  {"id": "E882080B-CA6E-434A-87B3-E33CD37A83D4", "gaps": []},
  {"id": "031F48A8-0CCB-4FCE-A350-68F38CBB6509", "gaps": ["color_specific"]},
  # chunk03
  {"id": "B0039BB6-A5BB-4F01-991D-1C10404F7FBB", "gaps": ["gender_violation", "color_specific"]},
  {"id": "4AB8283A-9233-4E5B-B201-833023743EE1", "gaps": []},
  {"id": "85A38407-53BD-4E59-B7F7-52A3D6AE8CE2", "gaps": ["gender_violation", "color_specific"]},
  {"id": "6F88607B-A16B-433B-81B5-AF4157AA1B9C", "gaps": ["gender_violation", "color_specific"]},
  {"id": "6DCCDA47-B32F-416A-92A2-DB93A0E02B1E", "gaps": ["color_specific"]},
  {"id": "2E5A165E-89F2-4022-8AA6-5EEE06B115C5", "gaps": ["truncated"]},
  {"id": "0BBA693A-48EC-43EC-BD4B-94C29116326A", "gaps": []},
  {"id": "901BEB26-8ABC-44F2-A15B-69F598F0B980", "gaps": ["gender_violation", "color_specific"]},
  {"id": "3C838EF7-FA72-458E-861F-31F4D3914ACF", "gaps": ["hallucination"]},
  {"id": "17AB9B7D-FBF3-49E4-A03E-E2D2ADDE9FC3", "gaps": ["gender_violation", "hallucination"]},
  # chunk04
  {"id": "53C90D44-1B39-4992-BB89-8EBAF98FB176", "gaps": []},
  {"id": "7BAC8528-1D5C-426C-8844-6A2883F00BB1", "gaps": ["gender_violation", "color_specific"]},
  {"id": "736E8BAB-4076-4756-AF3D-05F77E0E662B", "gaps": ["gender_violation", "color_specific"]},
  {"id": "561BACD8-D0B0-4AD1-84DC-5A709E962E58", "gaps": ["color_specific"]},
  {"id": "AB4F215B-8095-4BCB-A166-1204867FCC0D", "gaps": ["gender_violation", "color_specific"]},
  {"id": "787F8890-1F71-45A6-ADE9-FD7F9EDF39B7", "gaps": ["truncated"]},
  {"id": "89484425-5672-4CA1-A22F-61229C13D634", "gaps": ["color_specific"]},
  {"id": "368070BD-ADF7-4D04-9EAD-20D38D59D465", "gaps": ["gender_violation", "color_specific"]},
  {"id": "266DCCA9-D999-487C-90F1-6ED5E407178D", "gaps": ["gender_violation", "color_specific"]},
  {"id": "41A4AFC6-F081-4C37-ACAE-B5C3BCA2D58A", "gaps": ["gender_violation", "color_specific"]},
  # chunk05
  {"id": "1190BCCF-3AE3-428D-B798-F5F2235BE7B4", "gaps": ["color_specific"]},
  {"id": "49D657E8-DFC4-491C-AF11-83934F3042B1", "gaps": ["gender_violation", "color_specific"]},
  {"id": "BAD70829-E4A6-44F4-8BE0-99D6DD8DAD81", "gaps": []},
  {"id": "69900B11-1C47-4F71-AF1E-A983D70A64C7", "gaps": ["truncated"]},
  {"id": "70CF37F6-F6D4-4F41-A624-7C2C677C27D2", "gaps": ["wrong_count"]},
  {"id": "DB9EFDD0-15CD-4D32-8C61-21DE2C549D32", "gaps": ["hallucination", "missing_subject"]},
  {"id": "FC15C38E-D671-49F8-9494-2542E9E06D1E", "gaps": ["color_specific"]},
  {"id": "5A075B3A-FF3F-4825-871C-696C1D6901F2", "gaps": ["color_specific"]},
  {"id": "E4392A24-30E1-4766-AF47-C2FF32A57CFD", "gaps": []},
  {"id": "30BECB69-4939-44D9-A1C3-96D1063F66C6", "gaps": ["gender_violation", "color_specific"]},
  # chunk06
  {"id": "73757659-2FB0-4CCA-904E-BD387472BE95", "gaps": []},
  {"id": "43F2E5EB-C9D3-4905-9833-80F31443F88E", "gaps": []},
  {"id": "6837BD24-8163-4160-82A0-33C44F34304D", "gaps": ["gender_violation"]},
  {"id": "030425C8-3EA9-4E1E-931E-ECE85C600D85", "gaps": ["gender_violation", "color_specific"]},
  {"id": "CC15FA50-A288-4269-8FA5-BB5915C132E3", "gaps": []},
  {"id": "DE6B94DC-04C5-4F0C-80BA-C8E413FF00B5", "gaps": ["gender_violation", "color_specific"]},
  {"id": "A7E77B2B-0610-49DD-84DC-C33F1C8CC82A", "gaps": ["color_specific"]},
  {"id": "1F42BB57-0BB0-4D5A-B102-644001C883B3", "gaps": []},
  {"id": "59CA43DA-61C0-4646-8B61-470371CE486C", "gaps": ["gender_violation"]},
  {"id": "D7CAAAB4-5A2B-4F3E-ADFB-71043395BAA5", "gaps": ["too_short"]},
  # chunk07
  {"id": "7E269245-C43D-477F-A646-25C13928842E", "gaps": ["gender_violation", "color_specific"]},
  {"id": "228DB15B-AD75-41E1-B0E4-6FDC9148EF7E", "gaps": ["color_specific"]},
  {"id": "4CE4AA82-6BCC-49E4-933B-E6F6EBF790F7", "gaps": ["gender_violation", "color_specific", "hallucination"]},
  {"id": "F04CCF8E-6A2E-4FF0-9968-6BD554652882", "gaps": ["color_specific", "wrong_count"]},
  {"id": "06305125-5B14-44E7-9FB5-B11224AE16F8", "gaps": ["gender_violation", "color_specific", "wrong_count"]},
  {"id": "AC80474B-7E27-41B1-9630-66426229D259", "gaps": ["hallucination"]},
  {"id": "952C1882-4654-41BF-B905-8E89F386F023", "gaps": ["color_specific"]},
  {"id": "B0850E43-DBB2-4BB6-9739-356627AD2A53", "gaps": []},
  {"id": "9FFEFD2A-E4D0-4C97-B6C0-842AB1E23CB3", "gaps": []},
  {"id": "4DB158AC-A855-4A4E-96B2-8244D73C9F18", "gaps": ["gender_violation"]},
  # chunk08
  {"id": "2C86EDEB-C0F3-476A-83A8-BAB73C6F2FF3", "gaps": ["gender_violation", "color_specific"]},
  {"id": "D353436B-1EDB-4812-973C-95C546E36901", "gaps": []},
  {"id": "59F5A2CB-BC7F-44A9-BAB6-B6CECEEBAAD9", "gaps": ["gender_violation"]},
  {"id": "3B844792-1F81-4C50-BE23-2A13E947D43F", "gaps": ["gender_violation", "color_specific"]},
  {"id": "4B6A84E4-EB12-4BF5-8173-C7C893EA723F", "gaps": ["color_specific", "hallucination"]},
  {"id": "0FD77D69-04D7-4EFC-AE06-94C563FC2F28", "gaps": []},
  {"id": "E326A964-CC2B-4AC9-825A-E01767DA0E7B", "gaps": ["gender_violation", "color_specific"]},
  {"id": "56E014E2-586D-4BF0-9B1E-8423E9F2D34B", "gaps": ["color_specific"]},
  {"id": "E4F39A3D-64A4-4FD2-9585-CF1254C5D41E", "gaps": ["gender_violation", "color_specific", "hallucination"]},
  {"id": "6F98D3B9-69EE-40B4-9D9B-1A55355CE7DC", "gaps": ["color_specific"]},
  # chunk09
  {"id": "1CFB26E7-72F5-4CFA-879A-C10AE735E1DD", "gaps": ["gender_violation"]},
  {"id": "CC7836A0-559B-4C08-B4F0-183394755AB5", "gaps": ["gender_violation", "color_specific"]},
  {"id": "9F3AB9B7-69CE-4869-8875-A3F05319B92E", "gaps": ["gender_violation", "color_specific"]},
  {"id": "685B4970-EE5A-4BA1-A74B-795E59BBE1E0", "gaps": ["color_specific"]},
  {"id": "CA7A59D8-3AC3-4B27-95C9-207E3EA8FD5F", "gaps": ["gender_violation", "color_specific"]},
  {"id": "F4C98D25-4AD9-4192-B5C3-6D8DF9064970", "gaps": ["gender_violation", "color_specific"]},
  {"id": "1E506AEF-A5D6-4357-AC65-71634C0FDEEB", "gaps": ["gender_violation", "color_specific"]},
  {"id": "0B3B51A3-3930-46D0-8CEC-6EAC839D37F3", "gaps": ["color_specific"]},
  {"id": "FA870A9C-15D9-4370-BD0E-8E4FA26AC2DA", "gaps": ["gender_violation", "color_specific"]},
  {"id": "D695E4CD-513D-45CF-AE61-9EEA482BE3D9", "gaps": []},
  # chunk10
  {"id": "512FCA35-4A4C-44BA-885E-251B0A6D4A5A", "gaps": []},
  {"id": "DF4A7048-1BFF-4D73-A4F0-B3F3D0270403", "gaps": ["gender_violation", "color_specific"]},
  {"id": "606187A4-3BE0-4A28-8946-4589901D1EE7", "gaps": ["wrong_count"]},
  {"id": "9D4354F5-3DB3-4129-B250-610DA3BDD8E8", "gaps": []},
  {"id": "CEB079D2-18C6-4A0E-8B2B-25358C081BA6", "gaps": ["gender_violation", "color_specific"]},
  {"id": "09288560-9869-42DC-BCE9-D5F32BD31C38", "gaps": ["hallucination"]},
  {"id": "1B7B200A-B7F5-449B-BE60-1C375EEF3E3C", "gaps": ["wrong_count", "gender_violation", "color_specific"]},
  {"id": "49E74EEF-E61E-4FFD-A749-51261B3E1D47", "gaps": ["gender_violation"]},
  {"id": "4B932B43-E46D-4E3D-B6CF-2CFDBF6C0798", "gaps": ["gender_violation", "color_specific"]},
  {"id": "62F15AAB-5A1F-49E3-B2E5-173CEC714495", "gaps": ["gender_violation"]},
]

gap_map = {a["id"].upper(): a["gaps"] for a in annotations}
print(f"Annotations collected: {len(gap_map)}")

batch_path = "C:/dev/PhotoWell/tools/PromptEval/batches/batch_20260410_110245_rerun_20260410_113207_rerun_20260411_025932.json"
with open(batch_path) as f:
    batch = json.load(f)

photos = batch["photos"]
print(f"Photos in batch: {len(photos)}")

annotated_photos = []
missing = []
for p in photos:
    pid = p["id"].upper()
    if pid in gap_map:
        p = dict(p)
        p["gaps"] = gap_map[pid]
        p["oracle_desc"] = None
    else:
        missing.append(pid)
    annotated_photos.append(p)

if missing:
    print(f"MISSING annotations for: {missing}")
else:
    print("All photos annotated.")

batch["photos"] = annotated_photos
batch["annotated_at"] = "2026-04-11T00:00:00Z"

out_path = batch_path.replace(".json", "_annotated.json")
with open(out_path, "w") as f:
    json.dump(batch, f, indent=2)

print(f"Saved: {out_path}")
