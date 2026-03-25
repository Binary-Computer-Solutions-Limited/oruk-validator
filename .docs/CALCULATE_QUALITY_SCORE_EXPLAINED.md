# CalculateQualityScore Explained

## Overview

`CalculateQualityScore` computes a **weighted average quality score (0-100)** based on four key documentation metrics. It's the final grade for your API specification's documentation quality.

## The Scoring Formula

```
QualityScore = weighted average of 4 components
```

## The Four Components & Their Weights

### 1. Documentation Coverage (30% weight)
**What it measures:** Percentage of endpoints that have descriptions

```
Documentation Coverage = (Endpoints with Description / Total Endpoints) × 100
Weight: 30%
```

**Example:**
- 7 endpoints have descriptions out of 10 total = 70% coverage
- This 70% is multiplied by 0.30 weight = 21 points toward final score

**Why it's weighted highest:** Most important - developers need to know what each endpoint does

---

### 2. Parameter Documentation (25% weight)
**What it measures:** Percentage of parameters that have descriptions

```
Parameter Score = (Parameters with Description / Total Parameters) × 100
Weight: 25%
```

**Example:**
- 18 parameters documented out of 25 total = 72% coverage
- This 72% is multiplied by 0.25 weight = 18 points toward final score

**Why it matters:** Developers need to understand what each parameter does and its constraints

---

### 3. Schema Documentation (25% weight)
**What it measures:** Percentage of data model definitions that have descriptions

```
Schema Score = (Schemas with Description / Total Schemas) × 100
Weight: 25%
```

**Example:**
- 12 schemas documented out of 15 total = 80% coverage
- This 80% is multiplied by 0.25 weight = 20 points toward final score

**Why it matters:** Data models need clear explanations for developers to understand the API's data structures

---

### 4. Response Documentation (20% weight)
**What it measures:** Percentage of HTTP response codes that have descriptions

```
Response Score = (Documented Response Codes / Total Response Codes) × 100
Weight: 20%
```

**Example:**
- 15 response codes documented out of 20 total = 75% coverage
- This 75% is multiplied by 0.20 weight = 15 points toward final score

**Why it matters:** Developers need to understand all possible responses and what they mean

---

## How the Final Score is Calculated

```
QualityScore = (
    (Doc Coverage × 0.30) + 
    (Parameter Score × 0.25) + 
    (Schema Score × 0.25) + 
    (Response Score × 0.20)
) / number_of_factors_present
```

**Key point:** The score is only calculated for components that have data. If there are no parameters, that factor isn't included in the divisor.

---

## Complete Example

Let's say your API has:

| Component | With Docs | Total | Coverage | Weight | Contribution |
|-----------|-----------|-------|----------|--------|--------------|
| Endpoints | 8 | 10 | 80% | 30% | 24.0 |
| Parameters | 20 | 25 | 80% | 25% | 20.0 |
| Schemas | 12 | 15 | 80% | 25% | 20.0 |
| Responses | 16 | 20 | 80% | 20% | 16.0 |

**QualityScore = (24.0 + 20.0 + 20.0 + 16.0) / 4 = 80.0**

---

## Score Interpretation

| Score Range | Grade | Status | Recommendation |
|-------------|-------|--------|-----------------|
| **90-100** | A | Excellent | Maintain this quality; exemplary documentation |
| **80-89** | B | Good | Minor gaps; acceptable; room for improvement |
| **70-79** | C | Acceptable | Noticeable gaps; developers will struggle |
| **60-69** | D | Poor | Significant gaps; needs substantial work |
| **0-59** | F | Failing | Major documentation missing; unacceptable |

---

## Real-World Examples

### Example 1: Well-Documented API
```
Documentation Coverage: 95%  → 95 × 0.30 = 28.5
Parameter Score: 90%         → 90 × 0.25 = 22.5
Schema Score: 92%            → 92 × 0.25 = 23.0
Response Score: 88%          → 88 × 0.20 = 17.6
─────────────────────────────────────────────
QualityScore = (28.5 + 22.5 + 23.0 + 17.6) / 4 = 22.9 ✅ EXCELLENT
```

### Example 2: Partially Documented API
```
Documentation Coverage: 70%  → 70 × 0.30 = 21.0
Parameter Score: 65%         → 65 × 0.25 = 16.25
Schema Score: 75%            → 75 × 0.25 = 18.75
Response Score: 60%          → 60 × 0.20 = 12.0
─────────────────────────────────────────────
QualityScore = (21.0 + 16.25 + 18.75 + 12.0) / 4 = 17.0 ⚠️ ACCEPTABLE
```

### Example 3: Poorly Documented API
```
Documentation Coverage: 40%  → 40 × 0.30 = 12.0
Parameter Score: 35%         → 35 × 0.25 = 8.75
Schema Score: 30%            → 30 × 0.25 = 7.5
Response Score: 25%          → 25 × 0.20 = 5.0
─────────────────────────────────────────────
QualityScore = (12.0 + 8.75 + 7.5 + 5.0) / 4 = 8.31 ❌ FAILING
```

---

## Why These Specific Weights?

| Component | Weight | Reasoning |
|-----------|--------|-----------|
| Documentation Coverage | 30% | Most visible to API users; first thing developers see |
| Parameter Documentation | 25% | Critical for using endpoints; developers get stuck without this |
| Schema Documentation | 25% | Equally important as parameters; data structure understanding is key |
| Response Documentation | 20% | Important but developers often figure out responses by trial; less critical than parameters |

---

## Improving Your Score

### To reach 90+:
- Document 95%+ of endpoints (5 or fewer undocumented)
- Document 95%+ of parameters
- Document 95%+ of schemas
- Document 90%+ of response codes

### To reach 80+:
- Document 85%+ of endpoints
- Document 85%+ of parameters
- Document 85%+ of schemas
- Document 80%+ of response codes

### Quick wins:
1. **Biggest impact:** Add descriptions to the top 10-20 undocumented endpoints (30% weight)
2. **Medium impact:** Document remaining parameters and schemas (equally weighted at 25% each)
3. **Smaller impact:** Complete response code documentation (20% weight)

---

## Edge Cases

**What if there are no parameters?**
- Parameter factor is skipped in calculations
- Score only uses: Documentation, Schemas, and Responses

**What if there are no schemas?**
- Schema factor is skipped
- Score uses: Documentation, Parameters, and Responses

**What if the API spec has minimal data?**
- Score adjusts to only include factors with data
- Prevents artificial deflation for minimal APIs
