import Foundation
let input = URL(fileURLWithPath: CommandLine.arguments[1])
let workflow = try PropertyListSerialization.propertyList(from: Data(contentsOf: input), format: nil) as! [String: Any]
let actions = workflow["WFWorkflowActions"] as! [[String: Any]]
let replacements = actions.filter { ($0["WFWorkflowActionIdentifier"] as? String) == "is.workflow.actions.text.replace" }.map { $0["WFWorkflowActionParameters"] as! [String: Any] }
precondition(replacements.count == 4)
let rules = try replacements.map { (try NSRegularExpression(pattern: $0["WFReplaceTextFind"] as! String), $0["WFReplaceTextReplace"] as! String) }
func convert(_ text: String) -> String {
    rules.reduce(text) { result, rule in rule.0.stringByReplacingMatches(in: result, range: NSRange(result.startIndex..., in: result), withTemplate: rule.1) }
}
let sample = "https://x.com/eschatolocation/status/2107617817570709682?s=46"
let cases: [(String, String)] = [
    (sample, sample.replacingOccurrences(of: "x.com", with: "fxtwitter.com")),
    ("<https://twitter.com/user/status/123#fragment>", "<https://fxtwitter.com/user/status/123#fragment>"),
    ("https://www.x.com/user/status/123/photo/1", "https://fxtwitter.com/user/status/123/photo/1"),
    ("http://mobile.twitter.com/user/status/123", "http://fxtwitter.com/user/status/123"),
    ("https://x.com/i/web/status/123?s=46", "https://fxtwitter.com/i/web/status/123?s=46"),
    (sample + "\n" + sample, sample.replacingOccurrences(of: "x.com", with: "fxtwitter.com") + "\n" + sample.replacingOccurrences(of: "x.com", with: "fxtwitter.com")),
    ("https://x.com/user", "https://fxtwitter.com/user"),
    ("https://x.com.evil.example/user/status/123", "https://x.com.evil.example/user/status/123"),
    ("https://evil.example/https://x.com/user/status/123", "https://evil.example/https://x.com/user/status/123"),
    ("https://x.com/user/status/123abc", "https://fxtwitter.com/user/status/123abc"),
    ("https://fxtwitter.com/user/status/123", "https://fxtwitter.com/user/status/123"),
    ("ordinary text 🦊", "ordinary text 🦊")
]
for (input, expected) in cases {
    let result = convert(input)
    precondition(result == expected, "Conversion mismatch: \(input)")
}
let fixtureURL = input.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("link-tests.json")
let extraCases = try JSONSerialization.jsonObject(with: Data(contentsOf: fixtureURL)) as! [[String]]
for test in extraCases {
    precondition(convert(test[0]) == test[1], "Additional conversion mismatch")
    precondition(convert(test[1]) == test[1], "Conversion is not idempotent")
}
var outputs = Set<String>()
func validateReferences(_ object: Any) {
    if let dictionary = object as? [String: Any] {
        if let id = dictionary["OutputUUID"] as? String { precondition(outputs.contains(id), "Unresolved output reference") }
        for value in dictionary.values { validateReferences(value) }
    } else if let array = object as? [Any] {
        for value in array { validateReferences(value) }
    }
}
for action in actions {
    let params = action["WFWorkflowActionParameters"] as! [String: Any]
    validateReferences(params)
    if let id = params["UUID"] as? String { outputs.insert(id) }
}
let noInput = workflow["WFWorkflowNoInputBehavior"] as! [String: Any]
precondition(noInput["Name"] as? String == "WFWorkflowNoInputBehaviorGetClipboard")
for replacement in replacements {
    let token = replacement["WFInput"] as! [String: Any]
    precondition(token["WFSerializationType"] as? String == "WFTextTokenString")
}
print("Passed \(cases.count + extraCases.count) conversion cases and action-output reference validation. Device-level run still required.")
