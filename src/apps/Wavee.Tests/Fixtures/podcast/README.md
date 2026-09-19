# Podcast contract fixtures

vc3-594-replies.json is a minimal re-encode of verycomplex3.saz session 594 (2026-09-19), reduced to one reply. Author, identifiers, text, date, and reaction details are synthetic. No request headers, credentials, or raw archive bytes are included. Protobuf fixtures are constructed in behavior tests from declared schemas.

`vc4-275-comments.json` preserves the successful getCommentsForEntity page from verycomplex4.saz session 275. Author identities, avatars, content, timestamps, and entity identifiers are replaced with fixture values; nested reply and reaction author lists are empty. The response union, eligibility, pagination fields, booleans, and counts retain the captured shape.
